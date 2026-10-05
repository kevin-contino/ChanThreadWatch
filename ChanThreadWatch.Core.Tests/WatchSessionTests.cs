using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // WatchSession without a form: work for the owner's thread runs inline, and the settings
    // directory is a temporary folder so nothing is written next to the real settings.
    [TestClass]
    public class WatchSessionTests {
        private static readonly DateTime AddedOnUtc = new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime LastImageOnUtc = new DateTime(2020, 2, 20, 8, 0, 0, DateTimeKind.Utc);
        private string _dir;
        private string _threadListPath;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            // Logger writes next to the test binaries instead of AppData
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void Setup() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _threadListPath = Path.Combine(_dir, Settings.ThreadsFileName);
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Settings.DownloadFolder = Path.Combine(_dir, "downloads");
            Settings.DownloadFolderIsRelative = false;
            // Skips the one-time migration, which would save the settings and restart threads
            Settings.ChildThreadsAreNewFormat = true;
        }

        [TestCleanup]
        public void Cleanup() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private static string Ticks(DateTime utc) {
            return utc.Ticks.ToString();
        }

        // Every thread is stopped for a reason that keeps it stopped after the load, so
        // nothing is downloaded. The parent's page login can't be decrypted, so it is
        // written back unchanged.
        private static string[] SterileThreadListLines() {
            return new[] { "4",
                "https://boards.4chan.org/a/thread/1", StoredAuth.Prefix + "bm90IGEgcmVhbCBsb2dpbg==", "", "600", "0", @"Cat\a_1", "3", "Parent", Ticks(AddedOnUtc), Ticks(LastImageOnUtc), "", "Cat", "1",
                "https://boards.4chan.org/a/thread/2", "", "", "60", "1", "", "1", "Child", Ticks(AddedOnUtc), "", "4chan/a/1", "", "0",
                "https://boards.4chan.org/b/thread/3", "", "", "300", "0", "", "3", "Dead", Ticks(AddedOnUtc), "", "", "Other", "0" };
        }

        private static string Sha256(string path) {
            using (SHA256 sha = SHA256.Create()) {
                return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
            }
        }

        private WatchSession CreateSession() {
            return new WatchSession(a => a(), a => a(), _dir);
        }

        [TestMethod]
        // PendingUnix: the thread list holds a DPAPI login, and decrypting one throws off Windows, see MP-4c.
        // Off Windows the SaveDir Cat\a_1 is read as Cat/a_1 (MP-4b) and saved back in that form, so the
        // file is no longer byte for byte the same there; MP-4c has to compare it with that line changed.
        [TestCategory("PendingUnix")]
        public void ALoadedThreadListIsSavedBackUnchanged() {
            File.WriteAllLines(_threadListPath, SterileThreadListLines());
            string originalHash = Sha256(_threadListPath);
            WatchSession session = CreateSession();
            List<string> events = new List<string>();
            session.ThreadListLoadStarting += () => events.Add("start");
            session.ThreadWatcherCreated += w => events.Add("created " + w.PageID);
            session.ThreadWatcherAdded += w => events.Add("added " + w.PageID);
            session.AddedFromChanged += w => events.Add("addedFrom " + w.PageID);

            session.LoadThreadList();
            // The save must write a new file rather than leave the original in place
            File.Delete(_threadListPath);
            session.SaveThreadList();

            Assert.IsTrue(File.Exists(_threadListPath));
            Assert.AreEqual(originalHash, Sha256(_threadListPath));
            Assert.HasCount(3, session.ThreadWatchers);
            ThreadWatcher parent = GetWatcher(session, "4chan/a/1");
            Assert.AreSame(parent, GetWatcher(session, "4chan/a/2").ParentThread);
            Assert.IsNull(parent.ParentThread);
            CollectionAssert.AreEqual(new[] {
                "start",
                "created 4chan/a/1", "added 4chan/a/1",
                "created 4chan/a/2", "added 4chan/a/2",
                "created 4chan/b/3", "added 4chan/b/3",
                "addedFrom 4chan/a/1", "addedFrom 4chan/a/2", "addedFrom 4chan/b/3"
            }, events);
            Assert.IsFalse(session.IsLoadingThreadsFromFile);
        }

        // The form keeps a failed save pending, so the next timer tick tries again
        [TestMethod]
        public void SaveThreadListReportsWhetherTheListWasSaved() {
            WatchSession session = CreateSession();
            session.LoadThreadList();

            Assert.IsTrue(session.SaveThreadList());
            File.Delete(_threadListPath);
            // A folder where the file goes makes the save fail
            Directory.CreateDirectory(_threadListPath);

            Assert.IsFalse(session.SaveThreadList());
        }

        private static string ReadLog() {
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        private static int CountOf(string text, string part) {
            return text.Split(new[] { part }, StringSplitOptions.None).Length - 1;
        }

        // A failed save is tried again every minute, so only the first failure is logged in full,
        // and the next save that succeeds says how many failed
        [TestMethod]
        public void RepeatedSaveFailuresAreLoggedOnce() {
            WatchSession session = CreateSession();
            session.LoadThreadList();
            Directory.CreateDirectory(_threadListPath);
            Logger.Log("WatchSessionTests marker");
            int start = ReadLog().Length;

            Assert.IsFalse(session.SaveThreadList());
            Assert.IsFalse(session.SaveThreadList());
            Assert.IsFalse(session.SaveThreadList());
            Directory.Delete(_threadListPath);
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsTrue(session.SaveThreadList());

            string log = ReadLog().Substring(start);
            Assert.AreEqual(1, CountOf(log, "Exception: "), log);
            Assert.AreEqual(1, CountOf(log, "The thread list was saved after 3 failed saves."), log);
        }

        private static ThreadWatcher GetWatcher(WatchSession session, string pageID) {
            ThreadWatcher watcher;
            Assert.IsTrue(session.TryGetThreadWatcher(pageID, out watcher), "Missing watcher: " + pageID);
            return watcher;
        }

        [TestMethod]
        // PendingUnix: the thread list holds a DPAPI login, and decrypting one throws off Windows, see MP-4c
        [TestCategory("PendingUnix")]
        public void ADuplicateEntryFailsTheLoadAndKeepsTheFirst() {
            List<string> lines = new List<string>(SterileThreadListLines());
            // The third thread again, as a fourth entry with another description
            lines.AddRange(new[] { "https://boards.4chan.org/b/thread/3", "", "", "300", "0", "", "3", "Again", Ticks(AddedOnUtc), "", "", "", "0" });
            File.WriteAllLines(_threadListPath, lines);
            WatchSession session = CreateSession();

            session.LoadThreadList();

            Assert.HasCount(3, session.ThreadWatchers);
            Assert.AreEqual("Dead", GetWatcher(session, "4chan/b/3").Description);
            // The file that didn't fully load is copied aside before saving is allowed
            Assert.HasCount(1, Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(_threadListPath)));
        }

        [TestMethod]
        public void TheWatcherListIsACopy() {
            WatchSession session = CreateSession();
            session.RegisterThreadWatcher(new ThreadWatcher("https://boards.4chan.org/a/thread/1"), null);

            // Enumerating the live dictionary would throw once a watcher is added
            foreach (ThreadWatcher watcher in session.ThreadWatchers) {
                session.RegisterThreadWatcher(new ThreadWatcher("https://boards.4chan.org/a/thread/2"), null);
            }

            Assert.HasCount(2, session.ThreadWatchers);
        }

        [TestMethod]
        public void BlacklistingWritesTheFileAndBlocksTheThread() {
            string blacklistPath = Path.Combine(_dir, Settings.BlacklistFileName);
            File.WriteAllLines(blacklistPath, new[] { "4chan/a/9", "not a rule" });
            WatchSession session = CreateSession();
            session.LoadBlacklist();
            ThreadWatcher watcher = new ThreadWatcher("https://boards.4chan.org/a/thread/1");

            session.AddToBlacklist(new[] { watcher, watcher });

            CollectionAssert.AreEqual(new[] { "4chan/a/9", "4chan/a/1" }, File.ReadAllLines(blacklistPath));
            Assert.IsTrue(session.IsBlacklisted("4chan/a/1"));
            Assert.IsFalse(session.AddThread(new ThreadInfo { URL = "https://boards.4chan.org/a/thread/1" }));
            Assert.HasCount(0, session.ThreadWatchers);
        }

        // A watcher that is never started, with its folder (holding one file) in the download folder
        private static ThreadWatcher CreateThreadWithFolder(string url, string category, string folderName) {
            ThreadWatcher watcher = new ThreadWatcher(url) { Category = category };
            string categoryDir = category.Length != 0 ? Path.Combine(watcher.MainDownloadDirectory, category) : watcher.MainDownloadDirectory;
            watcher.ThreadDownloadDirectory = Path.Combine(categoryDir, folderName);
            Directory.CreateDirectory(watcher.ThreadDownloadDirectory);
            File.WriteAllText(Path.Combine(watcher.ThreadDownloadDirectory, "page.html"), folderName);
            return watcher;
        }

        [TestMethod]
        public void RemovingThreadsRemovesTheGivenThreadsInOrder() {
            WatchSession session = CreateSession();
            ThreadWatcher first = new ThreadWatcher("https://boards.4chan.org/a/thread/1");
            ThreadWatcher second = new ThreadWatcher("https://boards.4chan.org/a/thread/2");
            ThreadWatcher third = new ThreadWatcher("https://boards.4chan.org/a/thread/3");
            foreach (ThreadWatcher watcher in new[] { first, second, third }) session.RegisterThreadWatcher(watcher, null);
            List<string> events = new List<string>();
            session.ThreadWatcherRemoved += w => events.Add("removed " + w.PageID + " registered=" + session.IsThreadWatched(w.PageURL));
            session.SaveThreadListPending = false;

            // The pre-remove action fails for the first thread, which is removed anyway
            session.RemoveThreads(new[] { third, first }, w => {
                events.Add("pre " + w.PageID);
                if (w == third) throw new IOException("Locked");
            });

            CollectionAssert.AreEqual(new[] {
                "pre 4chan/a/3", "removed 4chan/a/3 registered=True",
                "pre 4chan/a/1", "removed 4chan/a/1 registered=True"
            }, events);
            CollectionAssert.AreEqual(new[] { second }, session.ThreadWatchers);
            Assert.IsTrue(session.SaveThreadListPending);
        }

        [TestMethod]
        public void RemovingCompletedThreadsMovesTheirFoldersToTheCompletedFolder() {
            string completedFolder = Path.Combine(_dir, "completed");
            Settings.MoveToCompletedFolder = true;
            Settings.CompletedFolder = completedFolder;
            Settings.CompletedFolderIsRelative = false;
            // Debug builds add a subfolder; if it were missing, the session would switch to the real Documents folder
            string completedDir = Settings.AbsoluteCompletedDirectory;
            Directory.CreateDirectory(completedDir);
            WatchSession session = CreateSession();
            ThreadWatcher first = CreateThreadWithFolder("https://boards.4chan.org/a/thread/1", "Cat", "a_1");
            ThreadWatcher second = CreateThreadWithFolder("https://boards.4chan.org/a/thread/2", "Cat", "a_2");
            ThreadWatcher uncategorized = CreateThreadWithFolder("https://boards.4chan.org/a/thread/3", String.Empty, "a_3");
            string categoryDir = Path.Combine(first.MainDownloadDirectory, "Cat");
            foreach (ThreadWatcher watcher in new[] { first, second, uncategorized }) session.RegisterThreadWatcher(watcher, null);

            session.RemoveCompletedThreads(new[] { first });

            Assert.AreEqual("a_1", File.ReadAllText(Path.Combine(completedDir, "Cat", "a_1", "page.html")));
            Assert.IsFalse(Directory.Exists(first.ThreadDownloadDirectory));
            // The category folder still holds the second thread
            Assert.IsTrue(Directory.Exists(categoryDir));
            Assert.HasCount(2, session.ThreadWatchers);

            session.RemoveCompletedThreads(new[] { second, uncategorized });

            Assert.AreEqual("a_2", File.ReadAllText(Path.Combine(completedDir, "Cat", "a_2", "page.html")));
            Assert.AreEqual("a_3", File.ReadAllText(Path.Combine(completedDir, "a_3", "page.html")));
            Assert.IsFalse(Directory.Exists(categoryDir));
            Assert.IsTrue(Directory.Exists(first.MainDownloadDirectory));
            Assert.HasCount(0, session.ThreadWatchers);
            Assert.AreEqual(completedFolder, Settings.CompletedFolder);
        }

        [TestMethod]
        public void RemovingIgnoresAThreadTheSessionDoesNotWatch() {
            WatchSession session = CreateSession();
            ThreadWatcher watcher = CreateThreadWithFolder("https://boards.4chan.org/a/thread/1", "Cat", "a_1");
            List<ThreadWatcher> removed = new List<ThreadWatcher>();
            session.ThreadWatcherRemoved += removed.Add;

            session.RemoveThreads(new[] { watcher }, WatchSession.DeleteThreadFolder);

            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "page.html")));
            Assert.HasCount(0, removed);
        }

        [TestMethod]
        public void RemovingCompletedThreadsKeepsTheFoldersUnlessMovingIsOn() {
            WatchSession session = CreateSession();
            ThreadWatcher watcher = CreateThreadWithFolder("https://boards.4chan.org/a/thread/1", "Cat", "a_1");
            session.RegisterThreadWatcher(watcher, null);

            session.RemoveCompletedThreads(new[] { watcher });

            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "page.html")));
            Assert.HasCount(0, session.ThreadWatchers);
        }

        [TestMethod]
        public void DeletingAThreadFolderDeletesItsEmptyCategoryFolder() {
            WatchSession session = CreateSession();
            ThreadWatcher first = CreateThreadWithFolder("https://boards.4chan.org/a/thread/1", "Cat", "a_1");
            ThreadWatcher second = CreateThreadWithFolder("https://boards.4chan.org/a/thread/2", "Cat", "a_2");
            ThreadWatcher uncategorized = CreateThreadWithFolder("https://boards.4chan.org/a/thread/3", String.Empty, "a_3");
            string categoryDir = Path.Combine(first.MainDownloadDirectory, "Cat");
            foreach (ThreadWatcher watcher in new[] { first, second, uncategorized }) session.RegisterThreadWatcher(watcher, null);

            session.RemoveThreads(new[] { first }, WatchSession.DeleteThreadFolder);

            Assert.IsFalse(Directory.Exists(first.ThreadDownloadDirectory));
            Assert.IsTrue(Directory.Exists(second.ThreadDownloadDirectory));

            session.RemoveThreads(new[] { second, uncategorized }, WatchSession.DeleteThreadFolder);

            Assert.IsFalse(Directory.Exists(categoryDir));
            Assert.IsFalse(Directory.Exists(uncategorized.ThreadDownloadDirectory));
            // The download folder itself is never deleted
            Assert.IsTrue(Directory.Exists(first.MainDownloadDirectory));
            Assert.HasCount(0, session.ThreadWatchers);
        }

        private static string[] StoppedThreadLines(string url, string saveDir) {
            return new[] { url, "", "", "600", "0", saveDir, "1", "T", Ticks(AddedOnUtc), "", "", "", "0" };
        }

        // A thread list as the Windows app writes it: CRLF line ends and backslash separators
        private void WriteWindowsThreadList(params string[][] threads) {
            List<string> lines = new List<string> { "4" };
            foreach (string[] thread in threads) lines.AddRange(thread);
            File.WriteAllText(_threadListPath, String.Join("\r\n", lines) + "\r\n");
        }

        // MP-4b acceptance: every SaveDir of a Windows-written thread list maps to its existing folder on
        // this OS. Each thread is stopped by the user, so nothing is downloaded.
        [TestMethod]
        public void AWindowsWrittenThreadListMapsEverySaveDirToItsFolder() {
            string downloadDir = Settings.AbsoluteDownloadDirectory;
            string[][] saveDirs = {
                new[] { @"Cat\a_1", "Cat", "a_1" },
                new[] { "b_2", "b_2" },
                new[] { @"Cat\Sub\c_3", "Cat", "Sub", "c_3" },
                new[] { @"..\Elsewhere\d_4", "..", "Elsewhere", "d_4" }
            };
            List<string[]> threads = new List<string[]>();
            for (int i = 0; i < saveDirs.Length; i++) {
                string[] parts = saveDirs[i];
                Directory.CreateDirectory(Path.Combine(downloadDir, Path.Combine(parts[1..])));
                threads.Add(StoppedThreadLines("https://boards.4chan.org/a/thread/" + (i + 1), parts[0]));
            }
            WriteWindowsThreadList(threads.ToArray());
            WatchSession session = CreateSession();

            session.LoadThreadList();

            Assert.HasCount(saveDirs.Length, session.ThreadWatchers);
            for (int i = 0; i < saveDirs.Length; i++) {
                string expected = Path.GetFullPath(Path.Combine(downloadDir, Path.Combine(saveDirs[i][1..])));
                ThreadWatcher watcher = GetWatcher(session, "4chan/a/" + (i + 1));
                Assert.AreEqual(expected, watcher.ThreadDownloadDirectory, saveDirs[i][0]);
                Assert.IsTrue(Directory.Exists(watcher.ThreadDownloadDirectory), saveDirs[i][0]);
            }
            // The list loaded fully, so it was not copied aside
            Assert.HasCount(0, Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(_threadListPath)));
        }

        // An absolute SaveDir of the other OS fails only its own thread, like any entry that doesn't load:
        // the error names SaveDir, the other threads load, and the file is copied aside before saving
        [TestMethod]
        public void AForeignAbsoluteSaveDirFailsOnlyItsThread() {
            string foreignDir = OperatingSystem.IsWindows() ? "/home/user/Threads/b_2" : @"C:\Threads\b_2";
            Directory.CreateDirectory(Path.Combine(Settings.AbsoluteDownloadDirectory, "a_1"));
            WriteWindowsThreadList(
                StoppedThreadLines("https://boards.4chan.org/a/thread/1", "a_1"),
                StoppedThreadLines("https://boards.4chan.org/a/thread/2", foreignDir));
            Logger.Log("WatchSessionTests foreign SaveDir marker");
            int start = ReadLog().Length;
            WatchSession session = CreateSession();

            session.LoadThreadList();

            Assert.HasCount(1, session.ThreadWatchers);
            GetWatcher(session, "4chan/a/1");
            string log = ReadLog().Substring(start);
            Assert.Contains("SaveDir holds \"" + foreignDir + "\"", log);
            Assert.HasCount(1, Directory.GetFiles(_dir, TextFile.GetCopySearchPattern(_threadListPath)));
        }

        [TestMethod]
        public void AForeignAbsoluteDownloadFolderFailsWithTheSettingName() {
            Settings.DownloadFolder = OperatingSystem.IsWindows() ? "/home/user/Threads" : @"C:\Threads";
            Settings.CompletedFolder = Settings.DownloadFolder;

            FormatException download = Assert.ThrowsExactly<FormatException>(() => Settings.AbsoluteDownloadDirectory);
            FormatException completed = Assert.ThrowsExactly<FormatException>(() => Settings.AbsoluteCompletedDirectory);

            Assert.Contains("DownloadFolder", download.Message);
            Assert.Contains("CompletedFolder", completed.Message);
        }

        // At startup a download folder that is an absolute path of another OS is reported with the setting
        // name, and the default folder is used for this session only. The setting is kept as written, so a
        // portable settings folder used on Windows and on Linux or macOS keeps its path for the other OS.
        [TestMethod]
        public void AForeignAbsoluteDownloadFolderIsUsedForTheSessionOnlyAndKept() {
            string foreign = OperatingSystem.IsWindows() ? "/home/user/Threads" : @"C:\Threads";
            Settings.DownloadFolder = foreign;
            Logger.Log("WatchSessionTests foreign DownloadFolder marker");
            int start = ReadLog().Length;

            WatchSession.EnsureDownloadFolderExists();

            string sessionFolder = WatchSession.GetDefaultFolder("Watched Threads");
            Assert.AreEqual(foreign, Settings.DownloadFolder);
            Assert.AreEqual(sessionFolder, Settings.DownloadFolderForSession);
            Assert.AreEqual(ExpectedAbsolute(sessionFolder), Settings.AbsoluteDownloadDirectory);
            Assert.Contains("DownloadFolder holds", ReadLog().Substring(start));
            string settingsPath = Path.Combine(_dir, "settings-saved.txt");
            Settings.Save(settingsPath);
            CollectionAssert.Contains(File.ReadAllLines(settingsPath), "DownloadFolder=" + foreign);
        }

        [TestMethod]
        public void ChangingTheDownloadFolderEndsTheSessionFolder() {
            Settings.DownloadFolder = OperatingSystem.IsWindows() ? "/home/user/Threads" : @"C:\Threads";
            WatchSession.EnsureDownloadFolderExists();
            string downloads = Path.Combine(_dir, "downloads");

            Settings.DownloadFolder = downloads;

            Assert.IsNull(Settings.DownloadFolderForSession);
            Assert.AreEqual(ExpectedAbsolute(downloads), Settings.AbsoluteDownloadDirectory);
        }

        // The completed folder falls back the same way when finished threads are moved
        [TestMethod]
        public void AForeignAbsoluteCompletedFolderIsUsedForTheSessionOnlyAndKept() {
            string foreign = OperatingSystem.IsWindows() ? "/home/user/Completed" : @"C:\Completed";
            Settings.CompletedFolder = foreign;
            Settings.MoveToCompletedFolder = true;
            WatchSession session = CreateSession();

            session.RemoveCompletedThreads(new ThreadWatcher[0]);

            Assert.AreEqual(foreign, Settings.CompletedFolder);
            Assert.AreEqual(WatchSession.GetDefaultFolder("Completed Threads"), Settings.CompletedFolderForSession);
        }

        // The Debug build keeps its files in a Debug subfolder of the download folder
        private static string ExpectedAbsolute(string folder) {
            #if DEBUG
                return Path.Combine(folder, Settings.DebugFolderName);
            #else
                return folder;
            #endif
        }

        // On Linux a Windows-written SaveDir finds its folder when only the case differs
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void AWindowsWrittenSaveDirFindsItsFolderIgnoringCaseOnLinux() {
            string existing = Path.Combine(Settings.AbsoluteDownloadDirectory, "Anime", "a_1");
            Directory.CreateDirectory(existing);
            WriteWindowsThreadList(StoppedThreadLines("https://boards.4chan.org/a/thread/1", @"anime\A_1"));
            WatchSession session = CreateSession();

            session.LoadThreadList();

            Assert.AreEqual(existing, GetWatcher(session, "4chan/a/1").ThreadDownloadDirectory);
        }

        // A relative download folder written on Windows is read with this OS's separators
        [TestMethod]
        public void ARelativeDownloadFolderWrittenOnWindowsIsReadOnThisOS() {
            Settings.DownloadFolder = @"rel\sub";
            Settings.DownloadFolderIsRelative = true;

            string expected = Path.GetFullPath(Path.Combine(Settings.ExeDirectory, "rel", "sub"));
            #if DEBUG
                expected = Path.Combine(expected, Settings.DebugFolderName);
            #endif
            Assert.AreEqual(expected, Settings.AbsoluteDownloadDirectory);
        }
    }
}
