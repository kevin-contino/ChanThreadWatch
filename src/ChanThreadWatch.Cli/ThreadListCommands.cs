using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JDP.Cli {
    // list, add and remove. The thread list is read and written only through ThreadListFile, so every thread ctw
    // does not add or remove is written back as it was read, saved logins included (see KeptStoredAuthProtector).
    internal static class ThreadListCommands {
        // list only reads: it takes no lock and creates nothing, also when the settings folder does not exist yet
        public static int List(CliContext context) {
            ThreadListData data = ReadThreadList(GetThreadListPath(context.GetSettingsFolder().Path));
            foreach (ThreadInfo thread in data.Threads) {
                context.Output.WriteLine(FormatListLine(thread));
            }
            if (data.TrailingLineCount != 0) {
                context.Error.WriteLine("ctw: warning: the end of " + Settings.ThreadsFileName + " is incomplete and was not read.");
            }
            return CliApp.ExitSuccess;
        }

        // URL, category and description separated by tabs, without the empty ones at the end
        internal static string FormatListLine(ThreadInfo thread) {
            string line = ConsoleText.CleanUrl(thread.URL) + "\t" + ConsoleText.Clean(thread.Category) + "\t" + ConsoleText.Clean(thread.Description);
            return line.TrimEnd('\t');
        }

        public static int Add(CliContext context) {
            CliCommand command = context.Command;
            string url = ThreadUrl.CleanForAdd(command.Url);
            string pageID = ThreadUrl.GetPageID(url);
            SettingsFolder folder = context.GetSettingsFolder();
            // The app creates its folder in the application data the same way
            if (folder.IsAppData) Directory.CreateDirectory(folder.Path);
            using (SettingsFolderAccess.Lock(folder.Path, "add")) {
                LoadSettings(Path.Combine(folder.Path, Settings.SettingsFileName));
                string path = GetThreadListPath(folder.Path);
                ThreadListData data = ReadThreadListForWrite(path);
                EnsureCanAdd(folder.Path, data, pageID);
                HashSet<string> entries = GetApiThreadEntriesWithout(folder.Path, data.Threads, null, pageID);
                data.Threads.Add(NewThread.Create(url, command.Description, command.Category));
                // First: if it fails, nothing is written. If the thread list write then fails, only the entry of a
                // thread that is not in the list is gone (the threads followed from it have entries of their own).
                WriteApiThreadEntriesForAdd(context, folder.Path, entries);
                TextFile.WriteAllLinesAtomic(path, ThreadListFile.Serialize(data.Threads));
            }
            context.Output.WriteLine("Added " + ConsoleText.CleanUrl(url) + " (settings folder: " + ConsoleText.Clean(folder.Path) + ")");
            return CliApp.ExitSuccess;
        }

        // The defaults for the new thread. A settings file that can't be read fails the command: Settings.Load would
        // log the failure, copy the file aside and go on with the built-in defaults. The file is read first through
        // SharedFile; ctw holds the folder's lock, so the app can't change it before Settings.Load reads it again.
        private static void LoadSettings(string path) {
            try {
                SharedFile.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException(Settings.SettingsFileName + " could not be read, so ctw does not add the thread: " + ex.Message);
            }
            Settings.Load(path);
        }

        // A thread already in the list (same page ID) is refused. The app's Add button instead applies its current
        // settings to a stopped thread and starts it again; ctw leaves that to the app. A blacklisted thread is
        // refused as in the app.
        private static void EnsureCanAdd(string folder, ThreadListData data, string pageID) {
            if (FindThreads(data, pageID).Count != 0) throw new CliException("The thread is already in the thread list.");
            if (IsBlacklisted(folder, pageID)) throw new CliException("The thread is blacklisted.");
        }

        // The blacklist file is read here rather than by WatchSession.LoadBlacklist, which logs a read failure and
        // goes on as if the file were empty
        private static bool IsBlacklisted(string folder, string pageID) {
            WatchSession session = new WatchSession(work => work(), work => work(), folder);
            session.AddBlacklistRules(ReadBlacklist(Path.Combine(folder, Settings.BlacklistFileName)));
            return session.IsBlacklisted(pageID);
        }

        private static string[] ReadBlacklist(string path) {
            try {
                return SharedFile.ReadAllLines(path) ?? new string[0];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException(Settings.BlacklistFileName + " could not be read, so ctw does not add the thread: " + ex.Message);
            }
        }

        // Refuses when more than one entry is the thread, so the user picks in the app. Downloaded files are never touched.
        public static int Remove(CliContext context) {
            string url = ThreadUrl.Clean(context.Command.Url);
            string pageID = ThreadUrl.GetPageID(url);
            string folder = context.GetSettingsFolder().Path;
            if (!Directory.Exists(folder)) throw new CliException("The thread is not in the thread list: there is no settings folder " + folder);
            ThreadInfo removed;
            using (SettingsFolderAccess.Lock(folder, "remove")) {
                string path = GetThreadListPath(folder);
                ThreadListData data = ReadThreadListForWrite(path);
                removed = FindSingleThread(data, pageID, url);
                // Worked out while the list still has the thread, which may link others to a marked thread
                HashSet<string> entries = GetApiThreadEntriesWithout(folder, data.Threads, removed, pageID);
                data.Threads.Remove(removed);
                TextFile.WriteAllLinesAtomic(path, ThreadListFile.Serialize(data.Threads));
                // After the thread list, so a failure never leaves a guarded thread without its entry; an entry
                // left without a thread is kept, as the app keeps one
                WriteApiThreadEntriesForRemove(context, folder, entries);
            }
            context.Output.WriteLine("Removed " + ConsoleText.CleanUrl(removed.URL) + " (settings folder: " + ConsoleText.Clean(folder) + ")");
            if (StoredLogins.HasLoginStoreItem(removed)) context.Error.WriteLine("ctw: note: " + StoredLogins.KeptItemNote);
            return CliApp.ExitSuccess;
        }

        // api-threads.txt (ApiThreadsFile) marks the threads added through the local API, which the app and ctw watch
        // guarded. A thread ctw adds is not one of them, so an entry left for its page ID goes, as when the app adds it.
        private static void WriteApiThreadEntriesForAdd(CliContext context, string folder, HashSet<string> entries) {
            try {
                WriteApiThreadEntries(context, folder, entries);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException(Settings.ApiThreadsFileName + " could not be written, so ctw does not add the thread: " + ex.Message);
            }
        }

        // A removed thread's entry goes, as when the app removes it
        private static void WriteApiThreadEntriesForRemove(CliContext context, string folder, HashSet<string> entries) {
            try {
                WriteApiThreadEntries(context, folder, entries);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                context.Error.WriteLine("ctw: warning: " + Settings.ApiThreadsFileName + " could not be written: " + ex.Message);
            }
        }

        // Null leaves the file as it is
        private static void WriteApiThreadEntries(CliContext context, string folder, HashSet<string> entries) {
            if (entries == null) return;
            TextFile.WriteAllLinesAtomic(Path.Combine(folder, Settings.ApiThreadsFileName), ApiThreadsFile.Serialize(entries));
            MarkApiThreadsFileWritten(context, folder);
        }

        // The entries of api-threads.txt once the thread (its page ID) leaves the list or stops being one the API added,
        // or null to leave the file as it is: it is missing, can't be read or has a version ctw does not know (the app
        // then guards every thread for its session, the safe side), or nothing changes. A load also marks the threads
        // whose AddedFrom chain reaches an entry (WatchSession.MarkGuardedThreads), also through threads that have no
        // entry; every such thread that stays gets an entry of its own, so no thread loses its mark with this one.
        private static HashSet<string> GetApiThreadEntriesWithout(string folder, List<ThreadInfo> threads, ThreadInfo leaving, string pageID) {
            HashSet<string> entries = TryReadApiThreads(Path.Combine(folder, Settings.ApiThreadsFileName));
            if (entries == null) return null;
            WatchSession.MarkGuardedThreads(threads, entries, false);
            if (!entries.Contains(pageID) && !IsGuarded(leaving)) return null;
            AddEntriesOfMarkedThreads(entries, threads, leaving);
            entries.Remove(pageID);
            return entries;
        }

        private static bool IsGuarded(ThreadInfo thread) {
            return thread != null && thread.Guarded;
        }

        private static void AddEntriesOfMarkedThreads(HashSet<string> entries, List<ThreadInfo> threads, ThreadInfo leaving) {
            foreach (ThreadInfo thread in threads) {
                if (thread == leaving || !thread.Guarded) continue;
                string pageID = ThreadUrl.TryGetPageID(thread.URL);
                if (pageID != null) entries.Add(pageID);
            }
        }

        private const string ApiThreadsFileWrittenName = "ApiThreadsFileWritten";

        // As the app after it writes api-threads.txt (WatchSession.MarkApiThreadsFileWritten), but the line is set in
        // the file itself, so every other byte of it stays (Settings.Save would rewrite all of it, and refuses a
        // plaintext login, which ctw can't encrypt). A missing settings file is not created, and a file ctw can't
        // change byte for byte is left as it is; either gives a warning. Under the folder's lock.
        private static void MarkApiThreadsFileWritten(CliContext context, string folder) {
            string path = Path.Combine(folder, Settings.SettingsFileName);
            try {
                SetApiThreadsFileWrittenInFile(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException) {
                context.Error.WriteLine("ctw: warning: " + ApiThreadsFileWrittenName + "=1 could not be saved in " + Settings.SettingsFileName + " (" + ex.Message +
                    "); until it is, a missing " + Settings.ApiThreadsFileName + " is read as no thread added through the local API.");
            }
        }

        private static void SetApiThreadsFileWrittenInFile(string path) {
            if (!File.Exists(path)) throw new FileNotFoundException("the file does not exist");
            byte[] updated = WithApiThreadsFileWritten(File.ReadAllBytes(path));
            if (updated != null) TextFile.WriteAllBytesAtomic(path, updated);
        }

        private static readonly UTF8Encoding _strictUtf8 = new UTF8Encoding(false, true);

        // The content with ApiThreadsFileWritten=1, or null if it is already on. The first line with the name in any
        // case (the one Settings.Load reads) gets the value; without one, the line is added at the end. A UTF-8 byte
        // order mark stays. Throws FormatException for a file that can't be changed byte for byte: UTF-16, not valid
        // UTF-8, or with a line break the app does not write (a lone CR).
        internal static byte[] WithApiThreadsFileWritten(byte[] content) {
            int bomLength = GetUtf8ByteOrderMarkLength(content);
            string updated = SetApiThreadsFileWritten(DecodeExactly(content, bomLength));
            if (updated == null) return null;
            byte[] body = _strictUtf8.GetBytes(updated);
            byte[] result = new byte[bomLength + body.Length];
            Buffer.BlockCopy(content, 0, result, 0, bomLength);
            Buffer.BlockCopy(body, 0, result, bomLength, body.Length);
            return result;
        }

        private static int GetUtf8ByteOrderMarkLength(byte[] content) {
            if (StartsWith(content, 0xFF, 0xFE) || StartsWith(content, 0xFE, 0xFF)) throw new FormatException("it is UTF-16, which ctw does not change");
            return StartsWith(content, 0xEF, 0xBB, 0xBF) ? 3 : 0;
        }

        private static bool StartsWith(byte[] content, params byte[] prefix) {
            if (content.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) {
                if (content[i] != prefix[i]) return false;
            }
            return true;
        }

        private static string DecodeExactly(byte[] content, int start) {
            string text;
            try {
                text = _strictUtf8.GetString(content, start, content.Length - start);
            }
            catch (DecoderFallbackException) {
                throw new FormatException("it is not valid UTF-8");
            }
            if (!_strictUtf8.GetBytes(text).AsSpan().SequenceEqual(content.AsSpan(start))) throw new FormatException("it does not read back byte for byte");
            if (Regex.IsMatch(text, "\r(?!\n)")) throw new FormatException("it has a line break the app does not write (a lone CR)");
            return text;
        }

        // Null if the setting is already on
        private static string SetApiThreadsFileWritten(string text) {
            Match line = Regex.Match(text, "^" + ApiThreadsFileWrittenName + "=([^\r\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!line.Success) return AppendLine(text, ApiThreadsFileWrittenName + "=1");
            Group value = line.Groups[1];
            return value.Value == "1" ? null : text.Substring(0, value.Index) + "1" + text.Substring(value.Index + value.Length);
        }

        // With the file's own line break
        private static string AppendLine(string text, string line) {
            string newLine = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : Environment.NewLine;
            bool endsWithLineBreak = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal);
            return text + (endsWithLineBreak ? "" : newLine) + line + newLine;
        }

        // A missing file costs no retries; one that goes away before the read is missing too
        private static HashSet<string> TryReadApiThreads(string path) {
            try {
                string[] lines = SharedFile.ReadAllLinesIfPresent(path);
                return lines != null ? ApiThreadsFile.Parse(lines) : null;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        private static ThreadInfo FindSingleThread(ThreadListData data, string pageID, string url) {
            List<ThreadInfo> found = FindThreads(data, pageID);
            if (found.Count == 0) throw new CliException("The thread is not in the thread list: " + ConsoleText.CleanUrl(url));
            if (found.Count > 1) {
                List<string> urls = found.ConvertAll(thread => ConsoleText.CleanUrl(thread.URL));
                throw new CliException(found.Count + " entries are this thread, so ctw removes none: " + String.Join(", ", urls) + ". Remove the one you mean in the app.");
            }
            return found[0];
        }

        // The page ID is the app's rule for "the same thread" (WatchSession.IsThreadWatched)
        private static List<ThreadInfo> FindThreads(ThreadListData data, string pageID) {
            return data.Threads.FindAll(thread => ThreadUrl.TryGetPageID(thread.URL) == pageID);
        }

        private static string GetThreadListPath(string folder) {
            return Path.Combine(folder, Settings.ThreadsFileName);
        }

        // A missing or empty file is an empty list, as in the app
        private static ThreadListData ReadThreadList(string path) {
            string[] lines = SharedFile.ReadAllLines(path) ?? new string[0];
            if (lines.Length == 0) return new ThreadListData { FileVersion = ThreadListFile.CurrentVersion };
            try {
                return ThreadListFile.Parse(lines);
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException) {
                throw new CliException(Settings.ThreadsFileName + " could not be read (" + ex.Message + "). Start Chan Thread Watch, which keeps a copy of it aside.");
            }
        }

        // Writing a list that didn't load fully would lose what didn't load, and the threads written back must
        // not hold a plaintext login (the app encrypts those on its next save)
        private static ThreadListData ReadThreadListForWrite(string path) {
            ThreadListData data = ReadThreadList(path);
            if (data.TrailingLineCount != 0) {
                throw new CliException("The end of " + Settings.ThreadsFileName + " is incomplete, so ctw does not change it. Start Chan Thread Watch, which keeps a copy of it aside.");
            }
            if (data.HasPlaintextAuth) {
                throw new CliException(Settings.ThreadsFileName + " holds logins saved without encryption by an older version. Start Chan Thread Watch once to encrypt them, then try again.");
            }
            return data;
        }
    }

    internal static class StoredLogins {
        public const string KeptItemNote = "The thread's saved login stays in the system's login store (Keychain or Secret Service); ctw never deletes it.";

        // ctw reads every stored login as one it can't decrypt (KeptStoredAuthProtector), so its stored value is there
        public static bool HasLoginStoreItem(ThreadInfo thread) {
            WatcherExtraData extraData = thread.ExtraData;
            return IsLoginStoreReference(extraData.UndecryptablePageAuth) || IsLoginStoreReference(extraData.UndecryptableImageAuth);
        }

        private static bool IsLoginStoreReference(string stored) {
            return StoredAuth.IsReference(stored, StoredAuth.KeychainPrefix) || StoredAuth.IsReference(stored, StoredAuth.SecretServicePrefix);
        }
    }

    internal static class ThreadUrl {
        // As the app's Add button: General.CleanPageURL, which adds http:// to an address without a scheme
        public static string Clean(string url) {
            string cleaned = General.CleanPageURL(url);
            if (cleaned == null) throw new CliException("Invalid URL: " + ConsoleText.CleanUrl(url));
            return cleaned;
        }

        // A login in the address would be saved as plaintext in the thread list, so it is refused (and not shown)
        public static string CleanForAdd(string url) {
            string cleaned = Clean(url);
            if (new Uri(cleaned).UserInfo.Length != 0) {
                throw new CliException("The URL holds a login (name:password@). Leave it out, and set the login in the app's thread settings instead.");
            }
            return cleaned;
        }

        // The site helper for the host (the generic one for an unknown host) gives the thread's page ID
        public static string GetPageID(string url) {
            SiteHelper siteHelper = SiteHelpers.GetInstance(new Uri(url).Host);
            siteHelper.SetURL(url);
            return siteHelper.GetPageID();
        }

        // Null for an entry whose URL is not a valid address
        public static string TryGetPageID(string url) {
            try {
                return GetPageID(url);
            }
            catch (UriFormatException) {
                return null;
            }
        }
    }
}
