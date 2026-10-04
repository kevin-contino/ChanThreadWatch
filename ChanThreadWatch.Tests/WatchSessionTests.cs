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
            return new WatchSession(a => a(), _dir);
        }

        [TestMethod]
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

        private static ThreadWatcher GetWatcher(WatchSession session, string pageID) {
            ThreadWatcher watcher;
            Assert.IsTrue(session.TryGetThreadWatcher(pageID, out watcher), "Missing watcher: " + pageID);
            return watcher;
        }

        [TestMethod]
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
    }
}
