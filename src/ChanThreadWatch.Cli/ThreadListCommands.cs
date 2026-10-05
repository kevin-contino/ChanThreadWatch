using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
                data.Threads.Add(NewThread.Create(url, command.Description, command.Category));
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
                data.Threads.Remove(removed);
                TextFile.WriteAllLinesAtomic(path, ThreadListFile.Serialize(data.Threads));
            }
            context.Output.WriteLine("Removed " + ConsoleText.CleanUrl(removed.URL) + " (settings folder: " + ConsoleText.Clean(folder) + ")");
            if (StoredLogins.HasLoginStoreItem(removed)) context.Error.WriteLine("ctw: note: " + StoredLogins.KeptItemNote);
            return CliApp.ExitSuccess;
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

    // Reads a file of the settings folder while another program may swap in a new version of it
    internal static class SharedFile {
        private const int ReadAttempts = 5;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

        // Returns null if the file (or its folder) does not exist. Shares writing and deleting, so the app can swap
        // in a new file while this reads; the swap is atomic (TextFile.WriteAllLinesAtomic), so this reads either
        // the old or the new file. A file that is missing for a moment or locked by another program is read again
        // a few times (about 200 ms in all).
        public static string[] ReadAllLines(string path) {
            for (int attempt = 1; ; attempt++) {
                try {
                    return ReadOnce(path);
                }
                catch (IOException ex) when (attempt < ReadAttempts && IsTransient(ex)) {
                    Thread.Sleep(RetryDelay);
                }
                catch (IOException ex) when (IsMissing(ex)) {
                    return null;
                }
            }
        }

        // Missing, or open in another program without sharing (a sharing or lock violation on Windows, flock's
        // EWOULDBLOCK on Unix, as for the settings folder's lock)
        private static bool IsTransient(IOException ex) {
            return IsMissing(ex) || SettingsFolderLock.IsHeldByAnother(ex);
        }

        private static bool IsMissing(IOException ex) {
            return ex is FileNotFoundException || ex is DirectoryNotFoundException;
        }

        private static string[] ReadOnce(string path) {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
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

    // A thread as the app's Add button creates it, from the settings that hold the main window's defaults
    // (frmChanThreadWatch.AddThread): no login, the "check every" interval unless it is a one-time download,
    // auto-follow, and no folder, stop reason or parent thread yet
    internal static class NewThread {
        public static ThreadInfo Create(string url, string description, string category) {
            bool oneTimeDownload = Settings.OneTimeDownload == true;
            return new ThreadInfo {
                URL = url,
                PageAuth = String.Empty,
                ImageAuth = String.Empty,
                CheckIntervalSeconds = GetCheckIntervalSeconds(oneTimeDownload),
                OneTimeDownload = oneTimeDownload,
                SaveDir = String.Empty,
                Description = description ?? String.Empty,
                StopReason = null,
                ExtraData = new WatcherExtraData { AddedOn = DateTime.Now, AddedFrom = String.Empty },
                Category = category ?? String.Empty,
                AutoFollow = Settings.AutoFollow == true
            };
        }

        // The app disables "check every" for a one-time download, which then saves 0
        private static int GetCheckIntervalSeconds(bool oneTimeDownload) {
            return oneTimeDownload ? 0 : GetCheckEveryMinutes() * 60;
        }

        // 3 minutes when the setting is missing. At startup the app changes a saved 1 to 0 ("1 or <") before it
        // shows it (frmChanThreadWatch constructor), so a 1 adds threads with 0.
        private static int GetCheckEveryMinutes() {
            int minutes = Settings.CheckEvery ?? 3;
            return minutes == 1 ? 0 : minutes;
        }
    }
}
