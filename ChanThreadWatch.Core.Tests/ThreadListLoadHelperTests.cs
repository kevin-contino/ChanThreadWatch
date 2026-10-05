using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The thread list helpers are internal members of WatchSession; each test uses a new
    // session. The check interval tests are in ChanThreadWatch.Tests, next to the main form.
    [TestClass]
    public class ThreadListLoadHelperTests {
        private string _dir;
        private WatchSession _session;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void Setup() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-migrate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            _session = new WatchSession(a => a(), a => a(), _dir);
        }

        [TestCleanup]
        public void Cleanup() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private ThreadWatcher AddWatcher(int threadNumber, string addedFrom) {
            ThreadWatcher watcher = new ThreadWatcher("https://boards.4chan.org/a/thread/" + threadNumber);
            watcher.Tag = new WatcherExtraData { AddedFrom = addedFrom };
            _session.RegisterThreadWatcher(watcher, null);
            return watcher;
        }

        private void Link(ThreadWatcher watcher) {
            _session.LinkToParentThread(watcher);
        }

        [TestMethod]
        public void ADuplicateThreadListEntryIsDetected() {
            AddWatcher(1, null);

            Assert.IsTrue(_session.IsThreadWatched("https://boards.4chan.org/a/thread/1"));
            Assert.IsFalse(_session.IsThreadWatched("https://boards.4chan.org/a/thread/2"));
        }

        // B4
        [TestMethod]
        public void LinkingToleratesAMissingAddedFrom() {
            ThreadWatcher watcher = AddWatcher(1, null);

            Link(watcher);

            Assert.IsNull(watcher.ParentThread);
        }

        [TestMethod]
        public void LinkingAddsTheChildToItsParent() {
            ThreadWatcher parent = AddWatcher(1, String.Empty);
            ThreadWatcher child = AddWatcher(2, parent.PageID);

            Link(parent);
            Link(child);

            Assert.AreSame(parent, child.ParentThread);
            Assert.IsTrue(parent.ChildThreads.ContainsKey(child.PageID));
            Assert.IsNull(parent.ParentThread);
        }

        // B6: a thread listing itself as its parent must not become its own child
        [TestMethod]
        public void LinkingIgnoresASelfReference() {
            ThreadWatcher watcher = AddWatcher(1, null);
            ((WatcherExtraData)watcher.Tag).AddedFrom = watcher.PageID;

            Link(watcher);

            Assert.IsNull(watcher.ParentThread);
            Assert.HasCount(0, watcher.ChildThreads);
        }

        // B6: two threads naming each other as parent must not form a cycle
        [TestMethod]
        public void LinkingIgnoresACycle() {
            ThreadWatcher a = AddWatcher(1, null);
            ThreadWatcher b = AddWatcher(2, a.PageID);
            ((WatcherExtraData)a.Tag).AddedFrom = b.PageID;

            Link(a);
            Link(b);

            bool cycle = a.ChildThreads.ContainsKey(b.PageID) && b.ChildThreads.ContainsKey(a.PageID);
            Assert.IsFalse(cycle);
            Assert.IsFalse(a.ParentThread == b && b.ParentThread == a);
            Assert.AreEqual(1, a.DescendantThreads.Count + b.DescendantThreads.Count);
        }

        private ThreadWatcher CreateWatcherInDownloadFolder(int threadNumber, string threadDir) {
            Settings.DownloadFolder = _dir;
            Settings.DownloadFolderIsRelative = false;
            ThreadWatcher watcher = new ThreadWatcher("https://boards.4chan.org/a/thread/" + threadNumber);
            watcher.ThreadDownloadDirectory = threadDir;
            return watcher;
        }

        // MP-4a: a thread folder on another root has a rooted "relative" path. Combined with the completed
        // folder, that made the destination the thread folder itself, so an empty one was deleted in place.
        // \\localhost\C$ reaches the local temp folder as a network share, another root.
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void CompletedMoveNeverDeletesAThreadFolderOnAnotherRootInPlace() {
            string localThreadDir = Path.Combine(_dir, "share", "thread1");
            Directory.CreateDirectory(localThreadDir);
            string shareThreadDir = @"\\localhost\" +localThreadDir.Substring(0, 1) + "$" + localThreadDir.Substring(2);
            if (!Directory.Exists(shareThreadDir)) Assert.Inconclusive("The administrative share " + shareThreadDir + " is not reachable");
            Settings.CompletedFolder = Path.Combine(_dir, "completed");
            Settings.CompletedFolderIsRelative = false;
            Directory.CreateDirectory(Settings.AbsoluteCompletedDirectory);
            ThreadWatcher watcher = CreateWatcherInDownloadFolder(1, shareThreadDir);
            Assert.IsTrue(Path.IsPathRooted(General.GetRelativeDirectoryPath(shareThreadDir, watcher.MainDownloadDirectory)));

            try {
                WatchSession.MoveThreadToCompletedFolder(watcher);
            }
            catch (IOException) {
                // Windows does not move a folder between roots; the folder must stay where it was
            }

            Assert.IsTrue(Directory.Exists(localThreadDir) || Directory.Exists(Path.Combine(Settings.AbsoluteCompletedDirectory, "thread1")));
        }

        // B6: a skipped move must not leave renaming disabled
        [TestMethod]
        public void SkippedMoveReenablesRenaming() {
            ThreadWatcher root = CreateWatcherInDownloadFolder(1, Path.Combine(_dir, "root"));
            ThreadWatcher child = CreateWatcherInDownloadFolder(2, Path.Combine(_dir, "root", "missing-child"));

            WatchSession.MoveDescendantThreadDirectory(root, child);

            Assert.IsFalse(child.DoNotRename);
            Assert.AreEqual(Path.Combine(_dir, "root", "missing-child"), child.ThreadDownloadDirectory);
        }

        // B6: a folder directly in the download folder stays where it is
        [TestMethod]
        public void FolderInTheDownloadFolderIsNotMoved() {
            string childDir = Path.Combine(_dir, "child");
            ThreadWatcher root = CreateWatcherInDownloadFolder(1, Path.Combine(_dir, "root"));
            ThreadWatcher child = CreateWatcherInDownloadFolder(2, childDir);

            Assert.AreEqual(childDir, WatchSession.GetDescendantThreadDestDir(root, child, childDir));
        }

        [TestMethod]
        public void NestedChildFolderMovesNextToTheRoot() {
            string childDir = Path.Combine(_dir, "root", "child");
            Directory.CreateDirectory(childDir);
            File.WriteAllText(Path.Combine(childDir, "image.jpg"), "x");
            ThreadWatcher root = CreateWatcherInDownloadFolder(1, Path.Combine(_dir, "root"));
            ThreadWatcher child = CreateWatcherInDownloadFolder(2, childDir);

            WatchSession.MoveDescendantThreadDirectory(root, child);

            Assert.AreEqual(Path.Combine(_dir, "child"), child.ThreadDownloadDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "child", "image.jpg")));
            Assert.IsFalse(child.DoNotRename);
        }

        [TestMethod]
        public void ChildWithoutAFolderIsSkipped() {
            ThreadWatcher root = CreateWatcherInDownloadFolder(1, Path.Combine(_dir, "root"));
            ThreadWatcher child = CreateWatcherInDownloadFolder(2, null);

            WatchSession.MoveDescendantThreadDirectory(root, child);

            Assert.IsNull(child.ThreadDownloadDirectory);
            Assert.IsFalse(child.DoNotRename);
        }
    }
}
