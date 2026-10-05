using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // S6: saved logins are encrypted at rest with DPAPI. All credentials here are fake.
    [TestClass]
    [SupportedOSPlatform("windows")]
    public class StoredAuthTests {
        private const string FakePageAuth = "fakepageuser:fakepagepass";
        private const string FakeImageAuth = "fakeimageuser:fakeimagepass";
        private static readonly DateTime AddedOnUtc = new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Utc);
        private string _dir;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-storedauth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup() {
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
                "https://boards.4chan.org/a/thread/1", pageAuth, imageAuth, "600", "0", "a_1", "", "Desc", AddedOnUtc.Ticks.ToString(), "", "", "Cat", "1" };
        }

        // Protects with DPAPI for this user, but with the wrong entropy
        private static string ProtectedWithOtherEntropy(string auth) {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(auth), Encoding.UTF8.GetBytes("some other app"), DataProtectionScope.CurrentUser);
            return StoredAuth.Prefix + Convert.ToBase64String(data);
        }

        private static string Tampered(string stored) {
            byte[] data = Convert.FromBase64String(stored.Substring(StoredAuth.Prefix.Length));
            data[data.Length / 2] ^= 0xFF;
            return StoredAuth.Prefix + Convert.ToBase64String(data);
        }

        private static string ThisSystemsPrefix() {
            if (OperatingSystem.IsWindows()) return StoredAuth.Prefix;
            return OperatingSystem.IsMacOS() ? StoredAuth.KeychainPrefix : StoredAuth.SecretServicePrefix;
        }

        // Values that can't be decrypted. On Windows they are DPAPI values that fail; elsewhere,
        // where no backend reads DPAPI, they are DPAPI values as copied from a Windows computer.
        private static string TamperedLogin(string auth) {
            return OperatingSystem.IsWindows() ? Tampered(StoredAuth.Protect(auth)) : UnavailableStoredAuthTests.CopiedFromWindows();
        }

        private static string OtherEntropyLogin(string auth) {
            return OperatingSystem.IsWindows() ? ProtectedWithOtherEntropy(auth) : UnavailableStoredAuthTests.CopiedFromWindows();
        }

        private List<ThreadInfo> SaveThreads(List<ThreadInfo> threads) {
            ThreadListStore store = new ThreadListStore();
            store.EndLoad(ThreadsPath, true);
            Assert.IsTrue(store.Save(ThreadsPath, threads));
            return new ThreadListStore().Read(ThreadsPath).Threads;
        }

        private static void AssertNoPlaintext(string path) {
            string content = File.ReadAllText(path);
            Assert.DoesNotContain("fakepage", content);
            Assert.DoesNotContain("fakeimage", content);
        }

        [TestMethod]
        public void ProtectedValueIsMarkedHidesThePlaintextAndRoundTrips() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            string stored = StoredAuth.Protect(FakePageAuth);

            Assert.StartsWith(ThisSystemsPrefix(), stored);
            Assert.IsTrue(StoredAuth.IsProtected(stored));
            Assert.DoesNotContain("fakepage", stored);
            Assert.AreEqual(FakePageAuth, StoredAuth.Unprotect(stored));
        }

        // Pins the on-disk format apart from DpapiStoredAuthProtector: the values 1.39 and later
        // wrote must keep decrypting with exactly this entropy and scope
        [TestMethod]
        // The DPAPI format exists only on Windows; other systems use their own backend
        [OSCondition(OperatingSystems.Windows)]
        public void ProtectedValueIsDpapiWithTheFixedEntropyAndCurrentUserScope() {
            string stored = StoredAuth.Protect(FakePageAuth);

            Assert.StartsWith("dpapi:", stored);
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring("dpapi:".Length)),
                Encoding.UTF8.GetBytes("JDP.ChanThreadWatch.StoredAuth.v1"), DataProtectionScope.CurrentUser);
            Assert.AreEqual(FakePageAuth, Encoding.UTF8.GetString(data));
        }

        // A Keychain or Secret Service reference copied from macOS or Linux is kept, never sent as a login
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow("keychain:0123456789abcdef0123456789abcdef")]
        [DataRow("secret-service:0123456789abcdef0123456789abcdef")]
        public void AReferenceFromMacOSOrLinuxIsKeptOnWindows(string reference) {
            File.WriteAllLines(ThreadsPath, Version4Lines(reference, ""));

            List<ThreadInfo> reloaded = SaveThreads(new ThreadListStore().Read(ThreadsPath).Threads);

            Assert.AreEqual(String.Empty, reloaded[0].PageAuth);
            Assert.AreEqual(reference, File.ReadAllLines(ThreadsPath)[2]);
        }

        // A legacy login that only starts like a reference is a login, so it is encrypted
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void ALegacyLoginThatLooksLikeAReferenceIsEncryptedOnWindows() {
            File.WriteAllLines(ThreadsPath, Version4Lines("keychain:user-pass", ""));

            List<ThreadInfo> reloaded = SaveThreads(new ThreadListStore().Read(ThreadsPath).Threads);

            Assert.StartsWith(StoredAuth.Prefix, File.ReadAllLines(ThreadsPath)[2]);
            Assert.AreEqual("keychain:user-pass", reloaded[0].PageAuth);
        }

        [TestMethod]
        public void LegacyPlaintextIsReturnedAsIs() {
            Assert.IsFalse(StoredAuth.IsProtected(FakePageAuth));
            Assert.AreEqual(FakePageAuth, StoredAuth.Unprotect(FakePageAuth));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void NullOrEmptyAuthStaysEmpty(string auth) {
            Assert.AreEqual(String.Empty, StoredAuth.Protect(auth));
            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(auth));
        }

        [TestMethod]
        public void LineBreaksAreFlattenedBeforeEncrypting() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            Assert.AreEqual("fake user:fake pass", StoredAuth.Unprotect(StoredAuth.Protect("fake\r\nuser:fake\npass")));
        }

        [TestMethod]
        [DataRow("tampered")]
        [DataRow("other entropy")]
        [DataRow("bad base64")]
        [DataRow("prefix only")]
        public void UndecryptableValueBecomesEmpty(string kind) {
            string stored = UndecryptableValue(kind);

            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(stored));
        }

        private static string UndecryptableValue(string kind) {
            switch (kind) {
                case "tampered": return TamperedLogin(FakePageAuth);
                case "other entropy": return OtherEntropyLogin(FakePageAuth);
                case "bad base64": return StoredAuth.Prefix + "not*base64!";
                default: return StoredAuth.Prefix;
            }
        }

        [TestMethod]
        public void ThreadListAuthIsEncryptedOnSaveAndRoundTrips() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines(FakePageAuth, FakeImageAuth)).Threads;

            List<ThreadInfo> reloaded = SaveThreads(threads);

            AssertNoPlaintext(ThreadsPath);
            string[] lines = File.ReadAllLines(ThreadsPath);
            Assert.IsTrue(StoredAuth.IsProtected(lines[2]));
            Assert.IsTrue(StoredAuth.IsProtected(lines[3]));
            Assert.AreEqual(FakePageAuth, reloaded[0].PageAuth);
            Assert.AreEqual(FakeImageAuth, reloaded[0].ImageAuth);
        }

        [TestMethod]
        public void LegacyPlaintextThreadListLoadsAndIsMigratedOnSave() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            File.WriteAllLines(ThreadsPath, Version4Lines(FakePageAuth, FakeImageAuth));

            ThreadListData data = new ThreadListStore().Read(ThreadsPath);
            Assert.AreEqual(FakePageAuth, data.Threads[0].PageAuth);
            Assert.AreEqual(FakeImageAuth, data.Threads[0].ImageAuth);

            List<ThreadInfo> reloaded = SaveThreads(data.Threads);

            AssertNoPlaintext(ThreadsPath);
            Assert.AreEqual(FakePageAuth, reloaded[0].PageAuth);
            Assert.AreEqual(FakeImageAuth, reloaded[0].ImageAuth);
        }

        [TestMethod]
        public void UndecryptableThreadListAuthBecomesEmptyAndKeepsTheThread() {
            string tampered = TamperedLogin(FakePageAuth);
            string otherEntropy = OtherEntropyLogin(FakeImageAuth);
            File.WriteAllLines(ThreadsPath, Version4Lines(tampered, otherEntropy));

            ThreadListData data = new ThreadListStore().Read(ThreadsPath);

            Assert.HasCount(1, data.Threads);
            Assert.AreEqual(0, data.TrailingLineCount);
            Assert.AreEqual("https://boards.4chan.org/a/thread/1", data.Threads[0].URL);
            Assert.AreEqual(String.Empty, data.Threads[0].PageAuth);
            Assert.AreEqual(String.Empty, data.Threads[0].ImageAuth);
            Assert.AreEqual("Cat", data.Threads[0].Category);
        }

        [TestMethod]
        public void BackupOfALegacyThreadListIsEncrypted() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            string[] backup = ThreadListFile.GetBackupLines(Version4Lines(FakePageAuth, FakeImageAuth));

            Assert.IsTrue(StoredAuth.IsProtected(backup[2]));
            Assert.IsTrue(StoredAuth.IsProtected(backup[3]));
            Assert.DoesNotContain("fakepage", String.Join("\n", backup));
            Assert.AreEqual(FakePageAuth, ThreadListFile.Parse(backup).Threads[0].PageAuth);
            Assert.AreEqual(FakeImageAuth, ThreadListFile.Parse(backup).Threads[0].ImageAuth);
        }

        [TestMethod]
        public void BackupOfAnInvalidThreadListIsRefused() {
            string[] truncated = Version4Lines(FakePageAuth, FakeImageAuth).Take(5).ToArray();

            Assert.IsNull(ThreadListFile.GetBackupLines(truncated));
            Assert.IsNull(ThreadListFile.GetBackupLines(new[] { "x" }));
        }

        [TestMethod]
        public void FailedDecryptIsLoggedWithoutTheValue() {
            string stored = TamperedLogin(FakePageAuth);
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            Logger.Log("StoredAuthTests marker");
            int start = ReadSharedFile(logPath).Length;

            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(stored));
            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(stored));

            // Only what this test logged; the log file is shared by the whole run. The
            // same value is reported once, so the periodic backup doesn't repeat it.
            string log = ReadSharedFile(logPath).Substring(start);
            Assert.AreEqual(1, log.Split(new[] { "A saved login could not be decrypted" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain(stored.Substring(StoredAuth.Prefix.Length, 24), log);
            Assert.DoesNotContain("fakepage", log);
        }

        // The logger keeps the log file open for appending
        private static string ReadSharedFile(string path) {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        // A temporary DPAPI failure must not erase the login: the stored value is written back as it was
        [TestMethod]
        public void UndecryptableThreadListAuthSurvivesSaveByteForByte() {
            File.WriteAllLines(ThreadsPath, Version4Lines(TamperedLogin(FakePageAuth), OtherEntropyLogin(FakeImageAuth)));
            byte[] original = File.ReadAllBytes(ThreadsPath);

            List<ThreadInfo> reloaded = SaveThreads(new ThreadListStore().Read(ThreadsPath).Threads);
            SaveThreads(reloaded);

            CollectionAssert.AreEqual(original, File.ReadAllBytes(ThreadsPath));
            Assert.AreEqual(String.Empty, reloaded[0].PageAuth);
            Assert.AreEqual(String.Empty, reloaded[0].ImageAuth);
        }

        [TestMethod]
        public void NewThreadListLoginReplacesAnUndecryptableOne() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            string otherEntropy = OtherEntropyLogin(FakeImageAuth);
            File.WriteAllLines(ThreadsPath, Version4Lines(TamperedLogin(FakePageAuth), otherEntropy));
            ThreadInfo thread = new ThreadListStore().Read(ThreadsPath).Threads[0];
            thread.PageAuth = "fakenewuser:fakenewpass";

            List<ThreadInfo> reloaded = SaveThreads(new List<ThreadInfo> { thread });

            Assert.AreEqual("fakenewuser:fakenewpass", reloaded[0].PageAuth);
            Assert.AreEqual(otherEntropy, File.ReadAllLines(ThreadsPath)[3]);
        }

        [TestMethod]
        public void ClearedUndecryptableThreadListLoginIsWrittenEmpty() {
            File.WriteAllLines(ThreadsPath, Version4Lines(TamperedLogin(FakePageAuth), ""));
            ThreadInfo thread = new ThreadListStore().Read(ThreadsPath).Threads[0];
            // What the edit form does when the login is changed to empty
            thread.ExtraData.UndecryptablePageAuth = null;

            SaveThreads(new List<ThreadInfo> { thread });

            Assert.AreEqual(String.Empty, File.ReadAllLines(ThreadsPath)[2]);
        }

        [TestMethod]
        public void BackupKeepsAnUndecryptableLogin() {
            string tampered = TamperedLogin(FakePageAuth);

            string[] backup = ThreadListFile.GetBackupLines(Version4Lines(tampered, ""));

            Assert.AreEqual(tampered, backup[2]);
        }

        [TestMethod]
        public void EmptyThreadListAuthStaysEmptyOnDisk() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines("", "")).Threads;

            List<ThreadInfo> reloaded = SaveThreads(threads);

            string[] lines = File.ReadAllLines(ThreadsPath);
            Assert.AreEqual(String.Empty, lines[2]);
            Assert.AreEqual(String.Empty, lines[3]);
            Assert.AreEqual(String.Empty, reloaded[0].PageAuth);
        }

        [TestMethod]
        public void SettingsAuthIsEncryptedOnSaveAndRoundTrips() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            Settings.Load(SettingsPath);
            Settings.PageAuth = FakePageAuth;
            Settings.ImageAuth = FakeImageAuth;

            Settings.Save(SettingsPath);
            Settings.Load(SettingsPath);

            AssertNoPlaintext(SettingsPath);
            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            Assert.AreEqual(FakeImageAuth, Settings.ImageAuth);
        }

        [TestMethod]
        public void LegacyPlaintextSettingsLoadAndAreMigratedOnSave() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + FakePageAuth, "imageauth=" + FakeImageAuth, "WindowTitle=fakepage title" });
            Settings.Load(SettingsPath);
            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            Assert.AreEqual(FakeImageAuth, Settings.ImageAuth);

            Settings.Save(SettingsPath);

            string[] lines = File.ReadAllLines(SettingsPath);
            Assert.IsTrue(StoredAuth.IsProtected(lines.Single(l => l.StartsWith("PageAuth=")).Substring("PageAuth=".Length)));
            Assert.IsTrue(StoredAuth.IsProtected(lines.Single(l => l.StartsWith("imageauth=")).Substring("imageauth=".Length)));
            Assert.Contains("WindowTitle=fakepage title", lines);
            Settings.Load(SettingsPath);
            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            Assert.AreEqual(FakeImageAuth, Settings.ImageAuth);
        }

        // A login that happens to start with the marker is still encrypted when set, so it
        // is not mistaken for an encrypted value when read back
        [TestMethod]
        public void AuthThatLooksEncryptedRoundTrips() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            const string markerLike = StoredAuth.Prefix + "fakepagepass";
            Settings.Load(SettingsPath);
            Settings.PageAuth = markerLike;
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines("", "")).Threads[0];
            thread.PageAuth = markerLike;

            Settings.Save(SettingsPath);
            Settings.Load(SettingsPath);
            List<ThreadInfo> reloaded = SaveThreads(new List<ThreadInfo> { thread });

            Assert.AreEqual(markerLike, Settings.PageAuth);
            Assert.AreEqual(markerLike, reloaded[0].PageAuth);
            AssertNoPlaintext(SettingsPath);
            AssertNoPlaintext(ThreadsPath);
        }

        [TestMethod]
        public void UndecryptableSettingsAuthBecomesEmpty() {
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + TamperedLogin(FakePageAuth), "UseSlug=1" });

            Settings.Load(SettingsPath);

            Assert.AreEqual(String.Empty, Settings.PageAuth);
            Assert.IsTrue(Settings.UseSlug);
        }

        // Mirrors the main form: the text box is filled from the setting and written back on exit
        [TestMethod]
        public void UndecryptableSettingsAuthSurvivesSaveByteForByte() {
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + TamperedLogin(FakePageAuth), "ImageAuth=" + OtherEntropyLogin(FakeImageAuth) });
            byte[] original = File.ReadAllBytes(SettingsPath);
            Settings.Load(SettingsPath);

            Settings.PageAuth = Settings.PageAuth ?? String.Empty;
            Settings.ImageAuth = Settings.ImageAuth ?? String.Empty;
            Settings.Save(SettingsPath);

            CollectionAssert.AreEqual(original, File.ReadAllBytes(SettingsPath));
            Settings.Load(SettingsPath);
            Assert.AreEqual(String.Empty, Settings.PageAuth);
        }

        [TestMethod]
        public void NewSettingsLoginReplacesAnUndecryptableOne() {
            // Needs a backend that keeps logins: DPAPI on Windows, a test keychain or Secret Service elsewhere
            TestLoginStore.RequireLoginStore();
            string otherEntropy = OtherEntropyLogin(FakeImageAuth);
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + TamperedLogin(FakePageAuth), "ImageAuth=" + otherEntropy });
            Settings.Load(SettingsPath);

            Settings.PageAuth = "fakenewuser:fakenewpass";
            Settings.Save(SettingsPath);
            Settings.Load(SettingsPath);

            Assert.AreEqual("fakenewuser:fakenewpass", Settings.PageAuth);
            Assert.Contains("ImageAuth=" + otherEntropy, File.ReadAllLines(SettingsPath));
        }

        [TestMethod]
        public void MissingSettingsAuthStaysNull() {
            Settings.Load(SettingsPath);

            Assert.IsNull(Settings.PageAuth);
            Settings.PageAuth = String.Empty;
            Settings.Save(SettingsPath);
            CollectionAssert.AreEqual(new[] { "PageAuth=" }, File.ReadAllLines(SettingsPath));
        }
    }
}
