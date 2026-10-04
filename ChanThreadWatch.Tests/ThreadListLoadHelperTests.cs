using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The thread list helpers are internal members of WatchSession; each test uses a new
    // session. The check interval helper is a private member of the main form, so it is
    // reached through reflection.
    [TestClass]
    public class ThreadListLoadHelperTests {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
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
            _session = new WatchSession(a => a(), _dir);
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

        private static int GetCheckEveryMinutes(bool comboEnabled, string text) {
            frmChanThreadWatch form = (frmChanThreadWatch)FormatterServices.GetUninitializedObject(typeof(frmChanThreadWatch));
            GC.SuppressFinalize(form);
            using (ComboBox cbo = new ComboBox { Enabled = comboEnabled })
            using (TextBox txt = new TextBox { Text = text }) {
                typeof(frmChanThreadWatch).GetField("cboCheckEvery", PrivateInstance).SetValue(form, cbo);
                typeof(frmChanThreadWatch).GetField("txtCheckEvery", PrivateInstance).SetValue(form, txt);
                MethodInfo method = typeof(frmChanThreadWatch).GetMethod("GetCheckEveryMinutes", PrivateInstance);
                try {
                    return (int)method.Invoke(form, null);
                }
                catch (TargetInvocationException ex) {
                    throw ex.InnerException;
                }
            }
        }

        // R11
        [TestMethod]
        public void CheckEveryUsesTheTextBoxNumber() {
            Assert.AreEqual(12, GetCheckEveryMinutes(false, "12"));
        }

        [TestMethod]
        [DataRow(false, "abc")]
        [DataRow(false, "99999999999")]
        [DataRow(true, "")]
        public void CheckEveryFallsBackToTheSavedSettingInsteadOfThrowing(bool comboEnabled, string text) {
            Assert.AreEqual(3, GetCheckEveryMinutes(comboEnabled, text));
            Settings.CheckEvery = 7;
            Assert.AreEqual(7, GetCheckEveryMinutes(comboEnabled, text));
        }
    }
}
