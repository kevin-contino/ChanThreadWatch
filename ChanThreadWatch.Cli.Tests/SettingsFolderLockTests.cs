using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // A second open of the lock file in this process is refused like one from another process (see
    // SettingsFolderLock), so the tests hold the lock here as the app would
    [TestClass]
    public class SettingsFolderLockTests : CliTestBase {
        private const string Url1 = "https://boards.4chan.org/a/thread/111";
        private const string Url2 = "https://boards.4chan.org/b/thread/222";

        [TestMethod]
        public void AppHoldsTheLock_AddAndRemoveAreRefusedAndTheFileIsUnchanged() {
            WriteThreadList(ThreadLines(Url1));
            byte[] before = File.ReadAllBytes(ThreadListPath);
            string expectedHolder = "Chan Thread Watch (pid " + Environment.ProcessId + " on " + SettingsFolderLockHolder.GetThisMachineName() + ")";
            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            using (appLock) {
                CliResult add = Run("add", Url2);
                CliResult remove = Run("remove", Url1);

                AssertFailed(add, CliApp.ExitFailure, expectedHolder + " is using the settings folder " + Folder + ". Close it, or add the thread in the app.");
                AssertFailed(remove, CliApp.ExitFailure, expectedHolder + " is using the settings folder " + Folder + ". Close it, or remove the thread in the app.");
                CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
                // The record is still the app's
                Assert.AreEqual(SettingsFolderLockHolder.WinForms, SettingsFolderLock.ReadHolder(Folder).Kind);
            }
        }

        [TestMethod]
        public void AnotherCommandHoldsTheLock_AddIsRefusedWithWaitAdvice() {
            SettingsFolderLock otherLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.Cli, out otherLock));
            using (otherLock) {
                CliResult add = Run("add", Url1);

                AssertFailed(add, CliApp.ExitFailure, "Another ctw command (pid " + Environment.ProcessId + " on ");
                StringAssert.Contains(add.Error, "Wait until it has finished, then try again.");
                Assert.IsFalse(File.Exists(ThreadListPath));
            }
        }

        [TestMethod]
        public void AppHoldsTheLock_ListStillReadsTheThreadList() {
            WriteThreadList(ThreadLines(Url1, category: "Cat"));
            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            using (appLock) {
                CliResult list = Run("list");

                AssertSucceeded(list);
                Assert.AreEqual(Url1 + "\tCat" + Environment.NewLine, list.Output);
            }
        }

        [TestMethod]
        public void AddAndRemove_ReleaseTheLockAndLeaveTheirRecord() {
            AssertSucceeded(Run("add", Url1));
            Assert.AreEqual(SettingsFolderLockHolder.Cli, SettingsFolderLock.ReadHolder(Folder).Kind);
            AssertSucceeded(Run("remove", Url1));

            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            appLock.Dispose();
        }

        [TestMethod]
        public void FailedCommand_ReleasesTheLock() {
            AssertFailed(Run("remove", Url1), CliApp.ExitFailure, "not in the thread list");

            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            appLock.Dispose();
        }

        [TestMethod]
        public void List_DoesNotTakeTheLock() {
            WriteThreadList(ThreadLines(Url1));

            AssertSucceeded(Run("list"));

            Assert.IsFalse(File.Exists(SettingsFolderLock.GetLockPath(Folder)));
        }

        // The app's window holds a named mutex for its settings folder, also when it started without the lock
        // ("start anyway") and in versions before 1.40.0, which take no lock at all
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void AppMutexForTheFolder_AddAndRemoveAreRefusedAndTheFileIsUnchanged() {
            WriteThreadList(ThreadLines(Url1));
            byte[] before = File.ReadAllBytes(ThreadListPath);
            string expected = "Chan Thread Watch is running with the settings folder " + Folder + " on this computer. Close it, or ";
            using (new System.Threading.Mutex(false, SettingsFolderLock.GetAppMutexName(Folder))) {
                AssertFailed(Run("add", Url2), CliApp.ExitFailure, expected + "add the thread in the app.");
                AssertFailed(Run("remove", Url1), CliApp.ExitFailure, expected + "remove the thread in the app.");
                CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
                // The lock was let go again
                SettingsFolderLock appLock;
                Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
                appLock.Dispose();
            }

            AssertSucceeded(Run("add", Url2));
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void AppMutexOfAnotherFolder_DoesNotBlock() {
            using (new System.Threading.Mutex(false, SettingsFolderLock.GetAppMutexName(Path.Combine(Folder, "other")))) {
                AssertSucceeded(Run("add", Url1));
            }
        }

        [TestMethod]
        public void DescribeHolder_UnknownOrMissingRecord() {
            Assert.AreEqual("Another program", SettingsFolderAccess.DescribeHolder(null));
            SettingsFolderLockHolder holder = new SettingsFolderLockHolder { Kind = "future", MachineName = "host\u001b[2J", ProcessId = 42 };
            Assert.AreEqual("Another program (pid 42 on host [2J)", SettingsFolderAccess.DescribeHolder(holder));
        }
    }
}
