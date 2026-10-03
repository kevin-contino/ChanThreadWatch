using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace JDP {
    // Format of the thread list file (threads.txt): the first line is the file version,
    // followed by a fixed number of lines per thread. SaveDir is kept as written in the
    // file (relative to the download folder, or empty). PageAuth and ImageAuth are written
    // encrypted (see StoredAuth); plaintext values from older versions still load, and
    // values that can't be decrypted are kept in ExtraData and written back unchanged.
    public static class ThreadListFile {
        public const int CurrentVersion = 4;

        // Returns 0 for unsupported file versions.
        public static int GetLinesPerThread(int fileVersion) {
            switch (fileVersion) {
                case 1: return 6;
                case 2: return 7;
                case 3: return 10;
                case 4: return 13;
                default: return 0;
            }
        }

        // Throws FormatException or OverflowException if the file is malformed. Lines after
        // the last complete thread are counted in TrailingLineCount instead of being parsed.
        public static ThreadListData Parse(string[] lines) {
            if (lines.Length == 0) throw new FormatException("The thread list file is empty.");
            int fileVersion = ParseInt(lines[0]);
            int linesPerThread = GetLinesPerThread(fileVersion);
            if (linesPerThread == 0) throw new FormatException("Unsupported thread list file version: " + lines[0]);
            ThreadListData data = new ThreadListData { FileVersion = fileVersion };
            int i = 1;
            while (i <= lines.Length - linesPerThread) {
                data.Threads.Add(ParseThreadInfo(lines, ref i, fileVersion));
            }
            data.TrailingLineCount = lines.Length - i;
            return data;
        }

        // True if a login is stored as plaintext (written by an older version). Only the
        // version line has to be valid, so this also checks a file that doesn't load.
        public static bool HasPlaintextAuth(string[] lines) {
            int fileVersion;
            Int32.TryParse(lines.Length != 0 ? lines[0] : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileVersion);
            int linesPerThread = GetLinesPerThread(fileVersion);
            return linesPerThread != 0 && HasPlaintextAuth(lines, linesPerThread, (lines.Length - 1) / linesPerThread);
        }

        // The two logins follow the URL at the start of each thread
        private static bool HasPlaintextAuth(string[] lines, int linesPerThread, int threadCount) {
            for (int t = 0; t < threadCount; t++) {
                int authLine = 2 + t * linesPerThread;
                if (StoredAuth.IsPlaintext(lines[authLine]) || StoredAuth.IsPlaintext(lines[authLine + 1])) return true;
            }
            return false;
        }

        // True if the lines form a complete thread list that Parse accepts.
        public static bool IsValid(string[] lines) {
            try {
                return Parse(lines).TrailingLineCount == 0;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException) {
                return false;
            }
        }

        // Returns the lines to write to the backup, or null if the file wouldn't load. The
        // threads are written again rather than copied, so logins from a file written by an
        // older version are encrypted in the backup too.
        public static string[] GetBackupLines(string[] lines) {
            ThreadListData data = TryParseFully(lines);
            return data != null ? Serialize(data.Threads) : null;
        }

        // Returns null if the lines don't form a complete thread list
        private static ThreadListData TryParseFully(string[] lines) {
            try {
                ThreadListData data = Parse(lines);
                return data.TrailingLineCount == 0 ? data : null;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException) {
                return null;
            }
        }

        private static ThreadInfo ParseThreadInfo(string[] lines, ref int i, int fileVersion) {
            ThreadInfo thread = new ThreadInfo { ExtraData = new WatcherExtraData() };
            thread.URL = lines[i++];
            ParseAuth(thread, lines[i++], lines[i++]);
            thread.CheckIntervalSeconds = ParseInt(lines[i++]);
            thread.OneTimeDownload = lines[i++] == "1";
            thread.SaveDir = lines[i++];
            if (fileVersion >= 2) {
                ParseStopReason(thread, lines[i++]);
            }
            if (fileVersion >= 3) {
                ParseDescriptionAndDates(thread, lines, ref i);
            }
            else {
                thread.Description = String.Empty;
                thread.ExtraData.AddedOn = DateTime.Now;
            }
            ParseVersion4Fields(thread, lines, ref i, fileVersion);
            return thread;
        }

        // A login that can't be decrypted is used as empty and kept to be written back unchanged.
        private static void ParseAuth(ThreadInfo thread, string storedPageAuth, string storedImageAuth) {
            thread.PageAuth = StoredAuth.Unprotect(storedPageAuth);
            thread.ImageAuth = StoredAuth.Unprotect(storedImageAuth);
            thread.ExtraData.UndecryptablePageAuth = StoredAuth.GetUndecryptable(storedPageAuth, thread.PageAuth);
            thread.ExtraData.UndecryptableImageAuth = StoredAuth.GetUndecryptable(storedImageAuth, thread.ImageAuth);
        }

        private static void ParseStopReason(ThreadInfo thread, string stopReasonLine) {
            if (stopReasonLine.Length != 0) {
                thread.StopReason = (StopReason)ParseInt(stopReasonLine);
            }
        }

        private static void ParseDescriptionAndDates(ThreadInfo thread, string[] lines, ref int i) {
            thread.Description = lines[i++];
            thread.ExtraData.AddedOn = new DateTime(ParseLong(lines[i++]), DateTimeKind.Utc).ToLocalTime();
            string lastImageOn = lines[i++];
            if (lastImageOn.Length != 0) {
                thread.ExtraData.LastImageOn = new DateTime(ParseLong(lastImageOn), DateTimeKind.Utc).ToLocalTime();
            }
        }

        private static void ParseVersion4Fields(ThreadInfo thread, string[] lines, ref int i, int fileVersion) {
            if (fileVersion >= 4) {
                thread.ExtraData.AddedFrom = lines[i++];
                thread.Category = lines[i++];
                thread.AutoFollow = lines[i++] == "1";
            }
            else {
                thread.ExtraData.AddedFrom = String.Empty;
                thread.Category = String.Empty;
            }
        }

        private static int ParseInt(string value) {
            return Int32.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static long ParseLong(string value) {
            long ticks = Int64.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) {
                throw new FormatException("Date out of range: " + value);
            }
            return ticks;
        }

        // Writes the threads in the current file version.
        public static string[] Serialize(IEnumerable<ThreadInfo> threads) {
            List<string> lines = new List<string>();
            lines.Add(CurrentVersion.ToString(CultureInfo.InvariantCulture));
            foreach (ThreadInfo thread in threads) {
                AddThreadLines(lines, thread);
            }
            return lines.ToArray();
        }

        private static void AddThreadLines(List<string> lines, ThreadInfo thread) {
            WatcherExtraData extraData = thread.ExtraData;
            lines.Add(TextFile.ToSingleLine(thread.URL));
            lines.Add(StoredAuth.ToStored(thread.PageAuth, extraData.UndecryptablePageAuth));
            lines.Add(StoredAuth.ToStored(thread.ImageAuth, extraData.UndecryptableImageAuth));
            lines.Add(thread.CheckIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            lines.Add(FormatBool(thread.OneTimeDownload));
            lines.Add(TextFile.ToSingleLine(thread.SaveDir));
            lines.Add(FormatStopReason(thread.StopReason));
            lines.Add(TextFile.ToSingleLine(thread.Description));
            lines.Add(FormatTicks(extraData.AddedOn));
            lines.Add(FormatTicks(extraData.LastImageOn));
            lines.Add(TextFile.ToSingleLine(extraData.AddedFrom));
            lines.Add(TextFile.ToSingleLine(thread.Category));
            lines.Add(FormatBool(thread.AutoFollow));
        }

        private static string FormatBool(bool value) {
            return value ? "1" : "0";
        }

        private static string FormatStopReason(StopReason? stopReason) {
            return stopReason != null ? ((int)stopReason.Value).ToString(CultureInfo.InvariantCulture) : String.Empty;
        }

        private static string FormatTicks(DateTime? date) {
            return date != null ? date.Value.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) : String.Empty;
        }
    }

    public class ThreadListData {
        public ThreadListData() {
            Threads = new List<ThreadInfo>();
        }

        public int FileVersion { get; set; }
        public List<ThreadInfo> Threads { get; private set; }
        public int TrailingLineCount { get; set; }
    }

    // Guards the thread list file against being overwritten after a failed load. Saving
    // stays disabled until EndLoad is called; if the load failed, the file is first copied
    // aside, and saving stays disabled for the session when that copy can't be made.
    public class ThreadListStore {
        private volatile bool _canSave;
        private bool _checkedBackup;

        public bool CanSave {
            get { return _canSave; }
        }

        // Returns null if the file doesn't exist or is empty. Throws if it can't be read or parsed.
        public ThreadListData Read(string path) {
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            return lines.Length != 0 ? ThreadListFile.Parse(lines) : null;
        }

        public void EndLoad(string path, bool loadedFully) {
            _canSave = loadedFully || TryPreserveFile(path);
        }

        private static bool TryPreserveFile(string path) {
            try {
                string copyPath = TextFile.PreserveCopy(path);
                Logger.Log("The thread list could not be fully loaded. The original file was kept as " + copyPath);
                return true;
            }
            catch (Exception ex) {
                Logger.Log("The thread list could not be fully loaded and could not be copied aside, so it will not be saved this session." + Environment.NewLine + ex);
                return false;
            }
        }

        // Returns false without writing anything if saving is disabled.
        public bool Save(string path, IEnumerable<ThreadInfo> threads) {
            if (!_canSave) return false;
            TextFile.WriteAllLinesAtomic(path, ThreadListFile.Serialize(threads));
            ProtectBackupOnce(path + ".bak");
            return true;
        }

        // A backup (see General.BackupThreadList) written by an older version holds plaintext
        // logins, and the periodic backup may never replace it (it can be turned off, or skip a
        // smaller list). So after the first save of the session, which writes encrypted
        // logins, the backup is rewritten with its own threads and encrypted logins. If that
        // fails (e.g. the file is in use), the next save tries again.
        private void ProtectBackupOnce(string backupPath) {
            if (_checkedBackup) return;
            try {
                ProtectBackup(backupPath);
                _checkedBackup = true;
            }
            catch (Exception ex) {
                Logger.Log("The thread list backup could not be rewritten with encrypted logins; the next save tries again." + Environment.NewLine + ex);
            }
        }

        // A backup that doesn't load fully is left as it is, since it can't be written again
        // without losing what didn't load
        private static void ProtectBackup(string backupPath) {
            if (!File.Exists(backupPath)) return;
            string[] lines = File.ReadAllLines(backupPath);
            if (!ThreadListFile.HasPlaintextAuth(lines)) return;
            string[] backupLines = ThreadListFile.GetBackupLines(lines);
            if (backupLines != null) {
                TextFile.WriteAllLinesAtomic(backupPath, backupLines);
            }
            else {
                Logger.Log("The thread list backup holds plaintext logins and could not be re-encrypted because it does not load fully. It was left unchanged.");
            }
        }
    }
}
