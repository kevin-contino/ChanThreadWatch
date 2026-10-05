using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // S6 on a system that can't keep logins (macOS and Linux until the Keychain and libsecret
    // backends, see MP-4c): a login is never written as plaintext, an encrypted value from
    // Windows is kept, and saving never fails or logs an exception because of a login. The
    // backend is swapped in on every system, so Windows runs these too. All credentials are fake.
    [TestClass]
    public class UnavailableStoredAuthTests {
        private const string FakePageAuth = "fakepageuser:fakepagepass";
        private const string FakeImageAuth = "fakeimageuser:fakeimagepass";
        private IStoredAuthProtector _savedProtector;
        private string _dir;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void UseTheUnavailableBackend() {
            _savedProtector = StoredAuth.Protector;
            StoredAuth.Protector = new UnavailableStoredAuthProtector();
            _dir = Path.Combine(Path.GetTempPath(), "ctw-noauth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup() {
            StoredAuth.Protector = _savedProtector;
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private string ThreadsPath {
            get { return Path.Combine(_dir, "threads.txt"); }
        }

        private string SettingsPath {
            get { return Path.Combine(_dir, "settings.txt"); }
        }

        private static string[] Version4Lines(string pageAuth, string imageAuth) {
            return new[] { "4",
                "https://boards.4chan.org/a/thread/1", pageAuth, imageAuth, "600", "0", "a_1", "", "Desc", "637146594000000000", "", "", "Cat", "1" };
        }

        // A DPAPI value as copied from a Windows computer: it can't be decrypted on this
        // system (nor on Windows). Unique, since each undecryptable value is logged once per session.
        internal static string CopiedFromWindows() {
            byte[] data = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
            return StoredAuth.Prefix + Convert.ToBase64String(data);
        }

        private static void AssertNoPlaintext(string path) {
            string content = File.ReadAllText(path);
            Assert.DoesNotContain("fakepage", content);
            Assert.DoesNotContain("fakeimage", content);
        }

        // The logger keeps the log file open for appending
        private static string ReadLog() {
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        [TestMethod]
        public void ALoginIsNeverReturnedAsTheValueToWrite() {
            Assert.IsFalse(StoredAuth.CanProtect);
            Assert.AreEqual(String.Empty, StoredAuth.Protect(FakePageAuth));
            Assert.AreEqual(String.Empty, StoredAuth.ToStored(FakePageAuth, null));
            // A login that looks encrypted is still a login
            Assert.AreEqual(String.Empty, StoredAuth.Protect(StoredAuth.Prefix + "fakepagepass"));
        }

        [TestMethod]
        public void AValueFromWindowsIsNotUsedAndIsKept() {
            string fromWindows = CopiedFromWindows();

            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(fromWindows));
            Assert.AreEqual(fromWindows, StoredAuth.ToStored(String.Empty, fromWindows));
            // A new login can't be kept, so the value from Windows stays for when the folder goes back
            Assert.AreEqual(fromWindows, StoredAuth.ToStored(FakePageAuth, fromWindows));
        }

        [TestMethod]
        public void ThreadListLoginsAreUsedForTheSessionAndWrittenEmpty() {
            File.WriteAllLines(ThreadsPath, Version4Lines(FakePageAuth, FakeImageAuth));
            ThreadListStore store = new ThreadListStore();
            List<ThreadInfo> threads = store.Read(ThreadsPath).Threads;
            store.EndLoad(ThreadsPath, true);

            Assert.IsTrue(store.Save(ThreadsPath, threads));

            AssertNoPlaintext(ThreadsPath);
            Assert.AreEqual(FakePageAuth, threads[0].PageAuth);
            ThreadInfo reloaded = new ThreadListStore().Read(ThreadsPath).Threads[0];
            Assert.AreEqual(String.Empty, reloaded.PageAuth);
            Assert.AreEqual(String.Empty, reloaded.ImageAuth);
            Assert.AreEqual("Cat", reloaded.Category);
        }

        [TestMethod]
        public void ThreadListKeepsAValueFromWindowsWhenANewLoginIsSet() {
            string fromWindows = CopiedFromWindows();
            File.WriteAllLines(ThreadsPath, Version4Lines(fromWindows, ""));
            ThreadListStore store = new ThreadListStore();
            ThreadInfo thread = store.Read(ThreadsPath).Threads[0];
            store.EndLoad(ThreadsPath, true);
            thread.PageAuth = FakePageAuth;

            Assert.IsTrue(store.Save(ThreadsPath, new List<ThreadInfo> { thread }));

            AssertNoPlaintext(ThreadsPath);
            Assert.AreEqual(fromWindows, File.ReadAllLines(ThreadsPath)[2]);
        }

        [TestMethod]
        public void SettingsLoginIsUsedForTheSessionAndNotWritten() {
            Settings.Load(SettingsPath);
            Settings.PageAuth = FakePageAuth;
            Settings.ImageAuth = "fakeimage\r\nuser:pass";

            Settings.Save(SettingsPath);

            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            Assert.AreEqual("fakeimage user:pass", Settings.ImageAuth);
            AssertNoPlaintext(SettingsPath);
            Settings.Load(SettingsPath);
            Assert.IsNull(Settings.PageAuth);
            Assert.IsNull(Settings.ImageAuth);
        }

        [TestMethod]
        public void ClearingASessionLoginWritesItEmpty() {
            Settings.Load(SettingsPath);
            Settings.PageAuth = FakePageAuth;

            Settings.PageAuth = String.Empty;
            Settings.Save(SettingsPath);

            Assert.AreEqual(String.Empty, Settings.PageAuth);
            CollectionAssert.AreEqual(new[] { "PageAuth=" }, File.ReadAllLines(SettingsPath));
        }

        [TestMethod]
        public void SettingsKeepAValueFromWindowsWhenASessionLoginIsSet() {
            string fromWindows = CopiedFromWindows();
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + fromWindows, "UseSlug=1" });
            Settings.Load(SettingsPath);
            Assert.AreEqual(String.Empty, Settings.PageAuth);

            Settings.PageAuth = FakePageAuth;
            Settings.Save(SettingsPath);

            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            CollectionAssert.AreEqual(new[] { "PageAuth=" + fromWindows, "UseSlug=1" }, File.ReadAllLines(SettingsPath));
        }

        [TestMethod]
        public void LegacyPlaintextSettingsAreUsedAndWrittenEmpty() {
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + FakePageAuth, "WindowTitle=title" });
            Settings.Load(SettingsPath);
            Assert.AreEqual(FakePageAuth, Settings.PageAuth);

            Settings.Save(SettingsPath);

            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            CollectionAssert.AreEqual(new[] { "PageAuth=", "WindowTitle=title" }, File.ReadAllLines(SettingsPath));
        }

        // Before MP-4c every save on Unix logged a PlatformNotSupportedException while the
        // backup held plaintext logins. The first save now rewrites the backup without them.
        [TestMethod]
        public void PlaintextBackupIsRewrittenWithoutLoginsAndSavesLogNoError() {
            string backupPath = ThreadsPath + ".bak";
            File.WriteAllLines(ThreadsPath, Version4Lines("", ""));
            File.WriteAllLines(backupPath, Version4Lines(FakePageAuth, FakeImageAuth));
            ThreadListStore store = new ThreadListStore();
            store.EndLoad(ThreadsPath, true);
            Logger.Log("UnavailableStoredAuthTests marker");
            int start = ReadLog().Length;

            Assert.IsTrue(store.Save(ThreadsPath, new List<ThreadInfo>()));
            Assert.IsTrue(store.Save(ThreadsPath, new List<ThreadInfo>()));
            Assert.IsTrue(store.Save(ThreadsPath, new List<ThreadInfo>()));

            AssertNoPlaintext(backupPath);
            string[] backup = File.ReadAllLines(backupPath);
            Assert.IsFalse(ThreadListFile.HasPlaintextAuth(backup));
            Assert.AreEqual("Cat", ThreadListFile.Parse(backup).Threads.Single().Category);
            string log = ReadLog().Substring(start);
            Assert.DoesNotContain("Exception", log);
            Assert.DoesNotContain("fakepage", log);
        }

        // The edit form's sequence for a stopped thread (frmChanThreadWatch.ApplyStoppedThreadEdit),
        // then the save. A session login keeps the value from Windows; clearing the login drops it.
        [TestMethod]
        [DataRow(FakePageAuth, true, DisplayName = "new login")]
        [DataRow("", false, DisplayName = "cleared login")]
        public void EditedLoginKeepsAValueFromWindowsUnlessCleared(string editedAuth, bool kept) {
            string fromWindows = CopiedFromWindows();
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(fromWindows, "")).Threads[0];
            WatcherExtraData extraData = thread.ExtraData;

            thread.PageAuth = editedAuth;
            extraData.UndecryptablePageAuth = StoredAuth.UndecryptableAfterEdit(extraData.UndecryptablePageAuth, editedAuth);
            string[] written = ThreadListFile.Serialize(new[] { thread });

            Assert.AreEqual(kept ? fromWindows : String.Empty, written[2]);
            Assert.DoesNotContain("fakepage", String.Join("\n", written));
        }

        // Where logins can be kept, any edited login replaces the undecryptable value
        [TestMethod]
        public void EditedLoginReplacesAnUndecryptableValueWhereLoginsAreKept() {
            StoredAuth.Protector = new KeepingProtector();

            Assert.IsNull(StoredAuth.UndecryptableAfterEdit(CopiedFromWindows(), FakePageAuth));
            Assert.IsNull(StoredAuth.UndecryptableAfterEdit(CopiedFromWindows(), String.Empty));
        }

        private sealed class KeepingProtector : IStoredAuthProtector {
            public bool CanProtect {
                get { return true; }
            }

            public string Protect(string line) {
                return StoredAuth.Prefix + "kept";
            }

            public string Unprotect(string stored) {
                return String.Empty;
            }
        }

        // Mirrors the main form: the text box is filled from the setting and written back on exit
        [TestMethod]
        public void ClearingASessionLoginKeepsAValueFromWindows() {
            string fromWindows = CopiedFromWindows();
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + fromWindows });
            Settings.Load(SettingsPath);
            Settings.PageAuth = FakePageAuth;

            Settings.PageAuth = String.Empty;
            Settings.Save(SettingsPath);

            Assert.AreEqual(String.Empty, Settings.PageAuth);
            CollectionAssert.AreEqual(new[] { "PageAuth=" + fromWindows }, File.ReadAllLines(SettingsPath));
        }

        // The periodic backup (General.BackupThreadList) writes the thread list again, so its
        // logins go through StoredAuth too. It uses the settings folder, which the tests point
        // at the test binaries' folder.
        [TestMethod]
        public void PeriodicBackupHoldsNoPlaintextLogin() {
            string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.ThreadsFileName);
            string backupPath = path + ".bak";
            Assert.IsFalse(File.Exists(path) || File.Exists(backupPath), "The test folder already holds a thread list");
            try {
                File.WriteAllLines(path, Version4Lines(FakePageAuth, FakeImageAuth));

                General.BackupThreadList();

                AssertNoPlaintext(backupPath);
                string[] backup = File.ReadAllLines(backupPath);
                Assert.IsFalse(ThreadListFile.HasPlaintextAuth(backup));
                Assert.AreEqual("Cat", ThreadListFile.Parse(backup).Threads.Single().Category);
            }
            finally {
                File.Delete(path);
                File.Delete(backupPath);
            }
        }
    }
}
