using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The app keeps one settings folder lock (Program.ReplaceSettingsFolderLock). Changing the
    // settings folder takes the new folder's lock before it lets go of the old one's.
    [TestClass]
    public class SettingsFolderLockSwapTests {
        private string _oldFolder;
        private string _newFolder;

        [TestInitialize]
        public void CreateFolders() {
            string root = Path.Combine(Path.GetTempPath(), "ctw-lockswap-" + Guid.NewGuid().ToString("N"));
            _oldFolder = Path.Combine(root, "old");
            _newFolder = Path.Combine(root, "new");
            Directory.CreateDirectory(_oldFolder);
            Directory.CreateDirectory(_newFolder);
        }

        [TestCleanup]
        public void ReleaseAndDelete() {
            Program.ReplaceSettingsFolderLock(null);
            Directory.Delete(Path.GetDirectoryName(_oldFolder), true);
        }

        private static SettingsFolderLock Acquire(string folder) {
            SettingsFolderLock folderLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(folder, SettingsFolderLockHolder.WinForms, out folderLock));
            return folderLock;
        }

        private static bool IsFree(string folder) {
            SettingsFolderLock folderLock;
            if (!SettingsFolderLock.TryAcquire(folder, SettingsFolderLockHolder.Cli, out folderLock)) return false;
            folderLock.Dispose();
            return true;
        }

        [TestMethod]
        public void ReplacingTheLockReleasesTheOldFolderOnly() {
            Program.ReplaceSettingsFolderLock(Acquire(_oldFolder));
            SettingsFolderLock newLock = Acquire(_newFolder);

            Assert.IsFalse(IsFree(_oldFolder));

            Program.ReplaceSettingsFolderLock(newLock);

            Assert.IsTrue(IsFree(_oldFolder));
            Assert.IsFalse(IsFree(_newFolder));
        }

        // A failed change disposes the new lock and keeps the old one
        [TestMethod]
        public void DisposingANewLockKeepsTheHeldOne() {
            Program.ReplaceSettingsFolderLock(Acquire(_oldFolder));
            Acquire(_newFolder).Dispose();

            Assert.IsFalse(IsFree(_oldFolder));
            Assert.IsTrue(IsFree(_newFolder));
        }

        // A window that started without the lock (the user chose to start anyway) takes it once
        // the other program lets go of it, and only then
        [TestMethod]
        public void MissingLockIsTakenOnceTheOtherProgramReleasesIt() {
            SettingsFolderLock other = Acquire(_oldFolder);
            try {
                Assert.IsFalse(Program.TryTakeMissingSettingsFolderLock(_oldFolder));
            }
            finally {
                other.Dispose();
            }

            Assert.IsTrue(Program.TryTakeMissingSettingsFolderLock(_oldFolder));

            Assert.IsFalse(IsFree(_oldFolder));
            Assert.AreEqual(SettingsFolderLockHolder.WinForms, SettingsFolderLock.ReadHolder(_oldFolder).Kind);
        }

        [TestMethod]
        public void HeldLockIsNotTakenAgain() {
            Program.ReplaceSettingsFolderLock(Acquire(_oldFolder));

            Assert.IsFalse(Program.TryTakeMissingSettingsFolderLock(_newFolder));

            Assert.IsTrue(IsFree(_newFolder));
        }

        [TestMethod]
        public void ReplacingWithNullReleasesTheLock() {
            Program.ReplaceSettingsFolderLock(Acquire(_oldFolder));

            Program.ReplaceSettingsFolderLock(null);

            Assert.IsTrue(IsFree(_oldFolder));
        }
    }
}
