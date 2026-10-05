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

        // The command line holds the lock only while it changes the thread list, so the window waits
        // past DefaultWait for it rather than refusing to start
        [TestMethod]
        public void WindowWaitsLongerForTheCommandLine() {
            SettingsFolderLock cliLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(_newFolder, SettingsFolderLockHolder.Cli, out cliLock));
            System.Threading.Tasks.Task release = System.Threading.Tasks.Task.Delay(SettingsFolderLock.DefaultWait + TimeSpan.FromSeconds(1)).ContinueWith(_ => cliLock.Dispose());

            SettingsFolderLock windowLock;
            bool acquired = Program.TryAcquireWaitingForCommandLine(_newFolder, true, out windowLock);

            release.Wait();
            Assert.IsTrue(acquired);
            windowLock.Dispose();
        }

        // The settings window switches folders on the UI thread, so it waits only DefaultWait even for the
        // command line, and the user can try again
        [TestMethod]
        public void SettingsWindowDoesNotWaitLongerForTheCommandLine() {
            SettingsFolderLock cliLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(_newFolder, SettingsFolderLockHolder.Cli, out cliLock));
            using (cliLock) {
                System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                SettingsFolderLock windowLock;

                Assert.IsFalse(Program.TryAcquireWaitingForCommandLine(_newFolder, false, out windowLock));
                Assert.IsLessThan(SettingsFolderLock.DefaultWait + TimeSpan.FromSeconds(3), elapsed.Elapsed);
            }
        }

        [TestMethod]
        public void WindowDoesNotWaitLongerForAnotherWindow() {
            using (Acquire(_newFolder)) {
                System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                SettingsFolderLock windowLock;

                Assert.IsFalse(Program.TryAcquireWaitingForCommandLine(_newFolder, true, out windowLock));
                Assert.IsLessThan(SettingsFolderLock.DefaultWait + TimeSpan.FromSeconds(3), elapsed.Elapsed);
            }
        }

        // ctw watch holds the lock until it is stopped, so the window refuses at once (DefaultWait for a program
        // that is closing, but not the command line's longer wait) and names ctw watch
        [TestMethod]
        public void WindowRefusesPromptlyWhileCtwWatchHoldsTheLock() {
            SettingsFolderLock watchLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(_newFolder, SettingsFolderLockHolder.Watch, out watchLock));
            using (watchLock) {
                System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                SettingsFolderLock windowLock;

                Assert.IsFalse(Program.TryAcquireWaitingForCommandLine(_newFolder, true, out windowLock));
                Assert.IsLessThan(SettingsFolderLock.DefaultWait + TimeSpan.FromSeconds(3), elapsed.Elapsed);
                HeldLockAction action = SettingsFolderLockHolder.Decide(SettingsFolderLock.ReadHolder(_newFolder), SettingsFolderLockHolder.WinForms, SettingsFolderLockHolder.GetThisMachineName());
                Assert.AreEqual(HeldLockAction.RefuseForWatch, action);
                Assert.AreEqual(Program.WatchHoldsFolderMessage, Program.GetHeldLockMessage(action));
            }
        }

        [TestMethod]
        public void HeldLockMessageNamesTheCommandLine() {
            StringAssert.Contains(Program.GetHeldLockMessage(HeldLockAction.WaitForCommandLine), "command line tool (ctw)");
            StringAssert.StartsWith(Program.GetHeldLockMessage(HeldLockAction.RefuseForWatch), "ctw watch is using this settings folder.");
            Assert.AreEqual(Program.SettingsFolderInUseMessage, Program.GetHeldLockMessage(HeldLockAction.Refuse));
        }
    }
}
