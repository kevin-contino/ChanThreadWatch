using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace JDP {
    // One program at a time uses a settings folder (the thread list and settings in it): the
    // window (which also takes its mutex, as older versions did) and the command line. The lock
    // is a file in the folder held open while the program runs, with a record of who holds it
    // (SettingsFolderLockHolder) so a program that is refused can say by whom:
    // - Windows: the holder opens it for reading and writing and shares it for reading only, so
    //   every other open for writing fails with a sharing violation while ReadHolder can still
    //   read it. This also works for a folder on an SMB share (portable mode from a NAS), where
    //   the server enforces it for all computers.
    // - Unix: the holder opens it with FileShare.None, for which .NET takes flock(LOCK_EX |
    //   LOCK_NB), so another .NET program's open fails. (.NET takes LOCK_SH for any other share
    //   mode, also for an open that only reads, so ReadHolder reads it with the C library
    //   instead.) flock is advisory and per open file, so two opens in one process exclude each
    //   other too. It is not taken when DOTNET_SYSTEM_IO_DISABLEFILELOCKING is set or when the
    //   file system does not support it (.NET then opens the file without a lock). Over NFS,
    //   Linux emulates flock with NFS byte-range locks, which need a working lock service; a
    //   CIFS mount may only lock on this computer. On such folders the lock may not exclude a
    //   program on another computer.
    // Versions before 1.40.0 don't take the lock. The operating system releases it when the
    // process ends, also after a crash, so a file left behind is not locked and the next
    // program takes it. The file is never deleted: a program that deleted it could let a third
    // one create and lock a new file while the second still holds the old one.
    public sealed class SettingsFolderLock : IDisposable {
        public const string FileName = "ctw.lock";

        // How long the window waits for a lock that a program which is closing may still hold
        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as HRESULTs
        private const int WindowsSharingViolation = unchecked((int)0x80070020);
        private const int WindowsLockViolation = unchecked((int)0x80070021);
        private const int LinuxWouldBlock = 11;
        private const int MacWouldBlock = 35;

        private readonly FileStream _stream;

        public string FolderPath { get; }

        private SettingsFolderLock(string folderPath, FileStream stream) {
            FolderPath = folderPath;
            _stream = stream;
        }

        public static string GetLockPath(string folderPath) {
            return Path.Combine(folderPath, FileName);
        }

        // Returns false (with folderLock null) if another program holds the folder's lock.
        // Otherwise writes the record of this program (hostKind, e.g. SettingsFolderLockHolder.WinForms)
        // into the file. Throws if the lock file can't be opened or written for another reason,
        // e.g. the folder is missing or read-only.
        public static bool TryAcquire(string folderPath, string hostKind, out SettingsFolderLock folderLock) {
            if (String.IsNullOrEmpty(folderPath)) throw new ArgumentException("The settings folder is empty.", nameof(folderPath));
            if (String.IsNullOrEmpty(hostKind)) throw new ArgumentException("The host kind is empty.", nameof(hostKind));
            folderLock = null;
            FileStream stream;
            try {
                stream = OpenForHolder(GetLockPath(folderPath));
            }
            catch (IOException ex) when (IsHeldByAnother(ex)) {
                return false;
            }
            folderLock = WriteRecord(folderPath, stream, SettingsFolderLockHolder.ForThisProcess(hostKind));
            return true;
        }

        // Like TryAcquire, but a held lock is tried again until the wait is over, for a program
        // that is still closing
        public static bool TryAcquire(string folderPath, string hostKind, TimeSpan wait, out SettingsFolderLock folderLock) {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!TryAcquire(folderPath, hostKind, out folderLock)) {
                if (elapsed.Elapsed >= wait) return false;
                Thread.Sleep(RetryInterval);
            }
            return true;
        }

        private static FileStream OpenForHolder(string lockPath) {
            FileShare share = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
        }

        private static SettingsFolderLock WriteRecord(string folderPath, FileStream stream, SettingsFolderLockHolder holder) {
            try {
                byte[] record = Encoding.UTF8.GetBytes(holder.Format());
                stream.SetLength(0);
                stream.Write(record, 0, record.Length);
                stream.Flush(true);
                return new SettingsFolderLock(folderPath, stream);
            }
            catch {
                stream.Dispose();
                throw;
            }
        }

        // A sharing violation on Windows; on Unix flock's EWOULDBLOCK, which .NET reports as
        // an IOException with the errno as its HResult
        internal static bool IsHeldByAnother(IOException ex) {
            if (OperatingSystem.IsWindows()) {
                return ex.HResult == WindowsSharingViolation || ex.HResult == WindowsLockViolation;
            }
            return ex.GetType() == typeof(IOException) && ex.HResult == (OperatingSystem.IsLinux() ? LinuxWouldBlock : MacWouldBlock);
        }

        // The record of the program that holds (or last held) the folder's lock. Null if there
        // is no lock file, or it can't be read, or it holds no valid record (a version that
        // writes none, or a holder that is just writing it).
        public static SettingsFolderLockHolder ReadHolder(string folderPath) {
            byte[] content = ReadRecord(GetLockPath(folderPath));
            return content != null ? SettingsFolderLockHolder.Parse(Encoding.UTF8.GetString(content)) : null;
        }

        private static readonly TimeSpan RereadDelay = TimeSpan.FromMilliseconds(200);

        // Like ReadHolder, but a missing record is read once more after a moment: a holder that
        // has just taken the lock may still be writing it (which can be slow on a network share)
        public static SettingsFolderLockHolder ReadHolderAfterRecordIsWritten(string folderPath) {
            SettingsFolderLockHolder holder = ReadHolder(folderPath);
            if (holder != null) return holder;
            Thread.Sleep(RereadDelay);
            return ReadHolder(folderPath);
        }

        // Null also when the C library can't be called (a Unix the runtime maps "libc" wrongly on)
        private static byte[] ReadRecord(string lockPath) {
            try {
                return OperatingSystem.IsWindows() ? ReadOnWindows(lockPath) : ReadOnUnix(lockPath);
            }
            catch (Exception ex) when (IsUnreadable(ex)) {
                return null;
            }
        }

        private static bool IsUnreadable(Exception ex) {
            return ex is IOException || ex is UnauthorizedAccessException || ex is DllNotFoundException || ex is EntryPointNotFoundException;
        }

        private const int MaxRecordSize = 4096;

        // Shares writing with the holder, which has the file open for writing
        private static byte[] ReadOnWindows(string lockPath) {
            using (FileStream fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                return ReadUpToMax(buffer => fs.Read(buffer, 0, buffer.Length));
            }
        }

        // Calls read (which fills the start of the buffer and returns the count, 0 at the end)
        // until the end or MaxRecordSize bytes
        private static byte[] ReadUpToMax(Func<byte[], long> read) {
            using (MemoryStream content = new MemoryStream()) {
                byte[] buffer = new byte[MaxRecordSize];
                long count;
                while (content.Length < MaxRecordSize && (count = read(buffer)) > 0) {
                    content.Write(buffer, 0, (int)count);
                }
                return content.ToArray();
            }
        }

        // open(2) and read(2) directly: any FileStream would take flock, which the holder's
        // exclusive lock refuses. Returns null if the file can't be opened.
        [UnsupportedOSPlatform("windows")]
        private static byte[] ReadOnUnix(string lockPath) {
            int fd = RetryWhileInterrupted(() => UnixOpen(lockPath, 0 /* O_RDONLY */));
            if (fd < 0) return null;
            try {
                // A failed read (-1) ends the content like the end of the file
                return ReadUpToMax(buffer => RetryWhileInterrupted(() => UnixRead(fd, buffer, buffer.Length)));
            }
            finally {
                UnixClose(fd);
            }
        }

        private const int EINTR = 4;

        // Calls again while the call fails (-1) because a signal interrupted it
        private static T RetryWhileInterrupted<T>(Func<T> call) where T : struct, IComparable<T> {
            T result;
            do {
                result = call();
            } while (result.CompareTo(default(T)) < 0 && Marshal.GetLastPInvokeError() == EINTR);
            return result;
        }

        // The runtime maps "libc" to the C library of each Unix (libc.so.6, /usr/lib/libc.dylib).
        // open is variadic (a third, mode argument is read only with O_CREAT). It is declared
        // without it on purpose: on Apple arm64 variadic arguments are passed differently from
        // fixed ones, so declaring a mode would pass it where open doesn't look.
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        // The array is pinned for the call, so what read writes into it is kept
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern nint UnixRead(int fd, [Out] byte[] buffer, nint count);

        [DllImport("libc", EntryPoint = "close")]
        private static extern int UnixClose(int fd);

        public void Dispose() {
            _stream.Dispose();
        }
    }

    // Who holds a settings folder's lock: one "key=value" per line
    public sealed class SettingsFolderLockHolder {
        public const string WinForms = "winforms";
        public const string Cli = "cli";
        public const string Service = "service";

        public string Kind { get; set; }
        public string MachineName { get; set; }
        public int ProcessId { get; set; }
        public DateTime StartedUtc { get; set; }

        public static SettingsFolderLockHolder ForThisProcess(string kind) {
            using (Process process = Process.GetCurrentProcess()) {
                return new SettingsFolderLockHolder {
                    Kind = kind,
                    MachineName = GetThisMachineName(),
                    ProcessId = Environment.ProcessId,
                    StartedUtc = process.StartTime.ToUniversalTime()
                };
            }
        }

        // The host name, which unlike Environment.MachineName (the NetBIOS name) is not cut to 15
        // characters, so two computers whose names start alike are told apart. The record is
        // written and compared with this same name.
        public static string GetThisMachineName() {
            try {
                return Dns.GetHostName();
            }
            catch (SocketException) {
                return Environment.MachineName;
            }
        }

        public string Format() {
            return "kind=" + TextFile.ToSingleLine(Kind) + "\n" +
                "machine=" + TextFile.ToSingleLine(MachineName) + "\n" +
                "pid=" + ProcessId.ToString(CultureInfo.InvariantCulture) + "\n" +
                "started=" + StartedUtc.ToString("o", CultureInfo.InvariantCulture) + "\n";
        }

        // Returns null unless every field is there and valid
        public static SettingsFolderLockHolder Parse(string text) {
            Dictionary<string, string> values = ParseLines(text);
            SettingsFolderLockHolder holder = new SettingsFolderLockHolder { Kind = GetValue(values, "kind"), MachineName = GetValue(values, "machine") };
            bool valid = holder.Kind != null && holder.MachineName != null && TryParseNumbers(holder, values);
            return valid ? holder : null;
        }

        private static bool TryParseNumbers(SettingsFolderLockHolder holder, Dictionary<string, string> values) {
            int processId;
            DateTime started;
            if (!Int32.TryParse(GetValue(values, "pid"), NumberStyles.None, CultureInfo.InvariantCulture, out processId) ||
                !DateTime.TryParseExact(GetValue(values, "started"), "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)) return false;
            holder.ProcessId = processId;
            holder.StartedUtc = started.ToUniversalTime();
            return true;
        }

        // Null if the key is missing or its value empty
        private static string GetValue(Dictionary<string, string> values, string key) {
            string value;
            return values.TryGetValue(key, out value) && value.Length != 0 ? value : null;
        }

        private static Dictionary<string, string> ParseLines(string text) {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Split('\n')) {
                int equals = line.IndexOf('=');
                if (equals > 0) values[line.Substring(0, equals)] = line.Substring(equals + 1).TrimEnd('\r');
            }
            return values;
        }

        // What a program of the given kind does when this holder has the lock: the window only
        // offers to start anyway when another window holds it on another computer (on this
        // computer the mutex already stopped it). Anything else, including a missing or
        // unreadable record, is refused.
        public static HeldLockAction Decide(SettingsFolderLockHolder holder, string hostKind, string thisMachineName) {
            if (hostKind != WinForms || holder == null || holder.Kind != WinForms) return HeldLockAction.Refuse;
            if (String.Equals(holder.MachineName, thisMachineName, StringComparison.OrdinalIgnoreCase)) return HeldLockAction.Refuse;
            return HeldLockAction.AskToStartAnyway;
        }
    }

    public enum HeldLockAction {
        Refuse,
        AskToStartAnyway
    }
}
