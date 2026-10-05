using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // S6 with a login store that keeps logins outside the file (Keychain, Secret Service): the
    // file holds a reference, each login keeps one item that is updated in place and deleted when
    // the login is cleared or its thread removed, and a store that can't be used falls back to
    // session-only logins. An in-memory store stands in for the system's, so these run on every
    // system. All credentials are fake.
    [TestClass]
    public class KeyringStoredAuthTests {
        private const string FakePageAuth = "fakepageuser:fakepagepass";
        private const string FakeImageAuth = "fakeimageuser:fakeimagepass";
        private IStoredAuthProtector _savedProtector;
        private FakeKeyring _keyring;
        private DateTime _utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private string _dir;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void UseAFakeLoginStore() {
            _savedProtector = StoredAuth.Protector;
            _keyring = new FakeKeyring();
            StoredAuth.Protector = new KeyringStoredAuthProtector(_keyring, () => _utcNow, TimeSpan.FromSeconds(10));
            // Deletes scheduled by other tests belong to their store
            StoredAuth.TakeScheduledDeletes();
            _dir = Path.Combine(Path.GetTempPath(), "ctw-keyring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup() {
            StoredAuth.TakeScheduledDeletes();
            StoredAuth.Protector = _savedProtector;
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private string SettingsPath {
            get { return Path.Combine(_dir, "settings.txt"); }
        }

        private static string[] Version4Lines(string pageAuth, string imageAuth) {
            return new[] { "4",
                "https://boards.4chan.org/a/thread/1", pageAuth, imageAuth, "600", "0", "a_1", "", "Desc", "637146594000000000", "", "", "Cat", "1" };
        }

        private string ThreadsPath {
            get { return Path.Combine(_dir, Settings.ThreadsFileName); }
        }

        // Stopped as "page not found", so a loaded thread is never started
        private static string[] StoppedThreadLines(string pageAuth, string imageAuth) {
            string[] lines = Version4Lines(pageAuth, imageAuth);
            lines[7] = "3";
            return lines;
        }

        private WatchSession CreateLoadedSession() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Settings.DownloadFolder = Path.Combine(_dir, "downloads");
            Settings.DownloadFolderIsRelative = false;
            Settings.ChildThreadsAreNewFormat = true;
            WatchSession session = new WatchSession(a => a(), a => a(), _dir);
            session.LoadThreadList();
            return session;
        }

        private static ThreadWatcher GetWatcher(WatchSession session) {
            return session.ThreadWatchers.Single();
        }

        private static string NewToken() {
            return StoredAuth.KeychainPrefix + Guid.NewGuid().ToString("N");
        }

        private static string ReadLog() {
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        private static int StartLog(string marker) {
            Logger.Log(marker);
            return ReadLog().Length;
        }

        [TestMethod]
        public void TheFileHoldsAReferenceWithARandom128BitId() {
            string page = StoredAuth.Protect(FakePageAuth);
            string image = StoredAuth.Protect(FakePageAuth);

            StringAssert.Matches(page, new Regex("^keychain:[0-9a-f]{32}$"));
            Assert.AreNotEqual(page, image);
            Assert.AreEqual(FakePageAuth, _keyring.Items[page.Substring(StoredAuth.KeychainPrefix.Length)]);
            Assert.AreEqual(FakePageAuth, StoredAuth.Unprotect(page));
        }

        // A value copied from another system is kept and never sent as a login
        [TestMethod]
        [DataRow("dpapi:AAAA")]
        [DataRow("keychain:0123456789abcdef0123456789abcdef")]
        [DataRow("secret-service:0123456789abcdef0123456789abcdef")]
        public void EveryBackendsValueIsRecognizedOnEverySystem(string stored) {
            Assert.IsTrue(StoredAuth.IsProtected(stored));
            Assert.IsFalse(StoredAuth.IsPlaintext(stored));
        }

        // Only the exact form is a reference, so such a login stays a login
        [TestMethod]
        [DataRow("keychain:user-pass")]
        [DataRow("keychain:0123456789ABCDEF0123456789ABCDEF")]
        [DataRow("keychain:0123456789abcdef0123456789abcde")]
        [DataRow("keychain:0123456789abcdef0123456789abcdef0")]
        [DataRow("secret-service:")]
        [DataRow("Keychain:0123456789abcdef0123456789abcdef")]
        public void ALoginThatLooksLikeAReferenceIsPlaintext(string login) {
            Assert.IsFalse(StoredAuth.IsProtected(login));
            Assert.IsTrue(StoredAuth.IsPlaintext(login));
            Assert.AreEqual(login, StoredAuth.Unprotect(login));
        }

        [TestMethod]
        public void ChangingALoginUpdatesItsItemInPlace() {
            string first = StoredAuth.Protect(FakePageAuth);

            string second = StoredAuth.Protect(FakeImageAuth, first);

            Assert.AreEqual(first, second);
            Assert.HasCount(1, _keyring.Items);
            Assert.AreEqual(FakeImageAuth, _keyring.Items.Values.Single());
        }

        [TestMethod]
        public void AnUnchangedLoginIsNotWrittenAgain() {
            string stored = StoredAuth.Protect(FakePageAuth);

            Assert.AreEqual(stored, StoredAuth.Protect(FakePageAuth, stored));
            Assert.AreEqual(stored, StoredAuth.Protect(FakePageAuth, stored));

            Assert.AreEqual(1, _keyring.Writes);
        }

        // E.g. deleted in Keychain Access while the app runs: the next save writes it again
        [TestMethod]
        public void AnItemDeletedOutsideTheAppIsWrittenAgain() {
            string stored = StoredAuth.Protect(FakePageAuth);
            _keyring.Items.Clear();

            Assert.AreEqual(stored, StoredAuth.Protect(FakePageAuth, stored));

            Assert.AreEqual(2, _keyring.Writes);
            Assert.AreEqual(FakePageAuth, _keyring.Items[stored.Substring(StoredAuth.KeychainPrefix.Length)]);
        }

        [TestMethod]
        [DataRow("secret-service:0123456789abcdef0123456789abcdef", DisplayName = "another store")]
        [DataRow("dpapi:AAAA", DisplayName = "DPAPI")]
        [DataRow("keychain:user-pass", DisplayName = "a login, not a reference")]
        public void AValueOfAnotherStoreIsNeitherReusedNorDeleted(string other) {
            string kept = StoredAuth.Protect(FakeImageAuth);

            string stored = StoredAuth.Protect(FakePageAuth, other);
            StoredAuth.ScheduleDelete(other);
            StoredAuthDeletes.Flush(_dir, null);

            StringAssert.Matches(stored, new Regex("^keychain:[0-9a-f]{32}$"));
            Assert.HasCount(2, _keyring.Items);
            Assert.AreEqual(0, _keyring.Clears);
            Assert.AreEqual(FakeImageAuth, StoredAuth.Unprotect(kept));
        }

        [TestMethod]
        public void ThreadListSavesKeepOneItemPerLogin() {
            List<ThreadInfo> threads = ThreadListFile.Parse(Version4Lines(FakePageAuth, FakeImageAuth)).Threads;

            string[] first = ThreadListFile.Serialize(threads);
            string[] second = ThreadListFile.Serialize(threads);
            string[] reloaded = ThreadListFile.Serialize(ThreadListFile.Parse(second).Threads);

            CollectionAssert.AreEqual(first, second);
            CollectionAssert.AreEqual(first, reloaded);
            Assert.HasCount(2, _keyring.Items);
            Assert.AreEqual(2, _keyring.Writes);
            Assert.DoesNotContain("fakepage", String.Join("\n", first));
            Assert.AreEqual(FakePageAuth, ThreadListFile.Parse(first).Threads[0].PageAuth);
        }

        [TestMethod]
        public void AChangedThreadLoginKeepsItsReference() {
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(FakePageAuth, "")).Threads[0];
            string before = ThreadListFile.Serialize(new[] { thread })[2];

            thread.PageAuth = "fakenewuser:fakenewpass";
            string after = ThreadListFile.Serialize(new[] { thread })[2];

            Assert.AreEqual(before, after);
            Assert.AreEqual("fakenewuser:fakenewpass", _keyring.Items.Values.Single());
        }

        [TestMethod]
        public void AClearedThreadLoginDeletesItsItem() {
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(FakePageAuth, FakeImageAuth)).Threads[0];
            string imageStored = ThreadListFile.Serialize(new[] { thread })[3];

            thread.PageAuth = String.Empty;
            string[] written = ThreadListFile.Serialize(new[] { thread });

            Assert.AreEqual(String.Empty, written[2]);
            Assert.AreEqual(imageStored, written[3]);
            // Deleted only once a save went through
            Assert.HasCount(2, _keyring.Items);
            StoredAuthDeletes.Flush(_dir, null);
            Assert.AreEqual(FakeImageAuth, _keyring.Items.Values.Single());
        }

        [TestMethod]
        public void RemovingAThreadDeletesItsItemsAfterTheNextSave() {
            File.WriteAllLines(ThreadsPath, StoppedThreadLines(FakePageAuth, FakeImageAuth));
            WatchSession session = CreateLoadedSession();
            Assert.IsTrue(session.SaveThreadList());
            Assert.HasCount(2, _keyring.Items);

            session.RemoveThreads(new[] { GetWatcher(session) });
            Assert.HasCount(2, _keyring.Items);
            Assert.IsTrue(session.SaveThreadList());

            Assert.HasCount(0, _keyring.Items);
        }

        // The app killed before its next save: the file still refers to the items
        [TestMethod]
        public void AThreadRemovedWithoutASaveKeepsItsItems() {
            File.WriteAllLines(ThreadsPath, StoppedThreadLines(FakePageAuth, FakeImageAuth));
            WatchSession session = CreateLoadedSession();
            Assert.IsTrue(session.SaveThreadList());
            string[] saved = File.ReadAllLines(ThreadsPath);

            session.RemoveThreads(new[] { GetWatcher(session) });
            StoredAuth.TakeScheduledDeletes();

            Assert.HasCount(2, _keyring.Items);
            Assert.AreEqual(FakePageAuth, ThreadListFile.Parse(saved).Threads[0].PageAuth);
        }

        [TestMethod]
        public void ARemovedThreadsLoginsSurviveInTheBackupAndResolveWhenItIsRestored() {
            File.WriteAllLines(ThreadsPath, StoppedThreadLines(FakePageAuth, FakeImageAuth));
            WatchSession session = CreateLoadedSession();
            Assert.IsTrue(session.SaveThreadList());
            File.Copy(ThreadsPath, ThreadsPath + ".bak");

            session.RemoveThreads(new[] { GetWatcher(session) });
            Assert.IsTrue(session.SaveThreadList());
            Assert.IsTrue(session.SaveThreadList());

            Assert.HasCount(2, _keyring.Items);
            File.Copy(ThreadsPath + ".bak", ThreadsPath, true);
            ThreadInfo restored = new ThreadListStore().Read(ThreadsPath).Threads[0];
            Assert.AreEqual(FakePageAuth, restored.PageAuth);
            Assert.AreEqual(FakeImageAuth, restored.ImageAuth);
        }

        // A copied file can give two threads one reference: removing one keeps the other's login
        [TestMethod]
        public void AReferenceAnotherThreadUsesIsNotDeleted() {
            string shared = StoredAuth.Protect(FakePageAuth);
            List<string> lines = new List<string>(StoppedThreadLines(shared, ""));
            lines.AddRange(StoppedThreadLines(shared, "").Skip(1).Select(line => line.Replace("thread/1", "thread/2")));
            File.WriteAllLines(ThreadsPath, lines);
            WatchSession session = CreateLoadedSession();
            ThreadWatcher first = session.ThreadWatchers.Single(w => w.PageURL.EndsWith("/1", StringComparison.Ordinal));

            session.RemoveThreads(new[] { first });
            Assert.IsTrue(session.SaveThreadList());

            Assert.HasCount(1, _keyring.Items);
            Assert.AreEqual(FakePageAuth, session.ThreadWatchers.Single().PageAuth);
        }

        [TestMethod]
        public void ALoadedListWithPlaintextLoginsIsSavedAtTheNextTick() {
            File.WriteAllLines(ThreadsPath, StoppedThreadLines(FakePageAuth, ""));

            WatchSession session = CreateLoadedSession();

            Assert.IsTrue(session.SaveThreadListPending);
        }

        // The periodic backup waits for the save that protects the list, so it never makes items of its own
        [TestMethod]
        public void BackupsOfAPlaintextListMakeNoItems() {
            string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.ThreadsFileName);
            string backupPath = path + ".bak";
            Assert.IsFalse(File.Exists(path) || File.Exists(backupPath), "The test folder already holds a thread list");
            try {
                File.WriteAllLines(path, Version4Lines(FakePageAuth, FakeImageAuth));

                General.BackupThreadList();
                General.BackupThreadList();
                General.BackupThreadList();

                Assert.HasCount(0, _keyring.Items);
                Assert.IsFalse(File.Exists(backupPath));
                List<ThreadInfo> threads = ThreadListFile.Parse(File.ReadAllLines(path)).Threads;
                File.WriteAllLines(path, ThreadListFile.Serialize(threads));
                General.BackupThreadList();
                General.BackupThreadList();
                Assert.HasCount(2, _keyring.Items);
                Assert.IsTrue(File.Exists(backupPath));
            }
            finally {
                File.Delete(path);
                File.Delete(backupPath);
            }
        }

        [TestMethod]
        public void ASettingsLoginIsUpdatedInPlaceAndDeletedWhenCleared() {
            Settings.Load(SettingsPath);
            Settings.PageAuth = FakePageAuth;
            Settings.Save(SettingsPath);
            string[] first = File.ReadAllLines(SettingsPath);

            Settings.PageAuth = FakeImageAuth;
            Settings.Save(SettingsPath);
            CollectionAssert.AreEqual(first, File.ReadAllLines(SettingsPath));
            Assert.AreEqual(FakeImageAuth, _keyring.Items.Values.Single());

            Settings.PageAuth = String.Empty;
            Settings.Save(SettingsPath);
            CollectionAssert.AreEqual(new[] { "PageAuth=" }, File.ReadAllLines(SettingsPath));
            Assert.HasCount(0, _keyring.Items);
        }

        [TestMethod]
        public void LegacyPlaintextSettingsGetOneItemAcrossSaves() {
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + FakePageAuth, "UseSlug=1" });
            Settings.Load(SettingsPath);

            Settings.Save(SettingsPath);
            string[] first = File.ReadAllLines(SettingsPath);
            Settings.Save(SettingsPath);

            CollectionAssert.AreEqual(first, File.ReadAllLines(SettingsPath));
            Assert.HasCount(1, _keyring.Items);
            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            Assert.DoesNotContain("fakepage", String.Join("\n", first));
        }

        // E.g. an item deleted in Keychain Access: the reference stays for when it comes back
        [TestMethod]
        public void AMissingItemIsKeptAndLoggedAsMissing() {
            string missing = NewToken();
            int start = StartLog("KeyringStoredAuthTests missing marker");

            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(missing, "")).Threads[0];
            string[] written = ThreadListFile.Serialize(new[] { thread });

            Assert.AreEqual(String.Empty, thread.PageAuth);
            Assert.AreEqual(missing, written[2]);
            Assert.IsTrue(StoredAuth.CanProtect);
            string log = ReadLog().Substring(start);
            Assert.Contains("was not found in the fake store", log);
            Assert.DoesNotContain(missing.Substring(StoredAuth.KeychainPrefix.Length), log);
        }

        // A store that fails after it worked (e.g. a keychain locked while running) is not used
        // again this session, and the logins it holds stay referenced in the files
        [TestMethod]
        public void AStoreThatFailsIsLoggedAsLockedAndItsValuesAreKept() {
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(FakePageAuth, "")).Threads[0];
            string stored = ThreadListFile.Serialize(new[] { thread })[2];
            int start = StartLog("KeyringStoredAuthTests locked marker");
            _keyring.Failure = new KeyringException("OSStatus -25308");

            string unreadable = NewToken();
            Assert.AreEqual(String.Empty, StoredAuth.Unprotect(unreadable));
            thread.PageAuth = "fakenewuser:fakenewpass";
            string[] written = ThreadListFile.Serialize(new[] { thread });

            Assert.IsFalse(StoredAuth.CanProtect);
            Assert.AreEqual(stored, written[2]);
            Assert.DoesNotContain("fakenew", String.Join("\n", written));
            string log = ReadLog().Substring(start);
            Assert.Contains("is locked or can't be used", log);
            Assert.AreEqual(1, Regex.Matches(log, "the fake store can't be used").Count);
        }

        // A failure is not for the whole session: the store is tried again after the cooldown
        [TestMethod]
        public void AStoreThatFailsOnceIsUsedAgainAfterTheCooldown() {
            int start = StartLog("KeyringStoredAuthTests cooldown marker");
            _keyring.Failure = new KeyringException("OSStatus -25308");
            Assert.AreEqual(String.Empty, StoredAuth.Protect(FakePageAuth));
            _keyring.Failure = null;

            _utcNow += KeyringStoredAuthProtector.RetryAfter - TimeSpan.FromSeconds(1);
            Assert.IsFalse(StoredAuth.CanProtect);
            _utcNow += TimeSpan.FromSeconds(2);
            string stored = StoredAuth.Protect(FakePageAuth);
            StoredAuth.Protect(FakeImageAuth);

            Assert.AreEqual(FakePageAuth, StoredAuth.Unprotect(stored));
            string log = ReadLog().Substring(start);
            Assert.AreEqual(1, Regex.Matches(log, "the fake store can't be used").Count);
            Assert.AreEqual(1, Regex.Matches(log, "the fake store can be used again").Count);
        }

        // A store waiting for the user (an unlock prompt, an access dialog) counts as failed after the timeout
        [TestMethod]
        public void AStoreCallThatDoesNotAnswerTimesOut() {
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                _keyring.Block = release;
                StoredAuth.Protector = new KeyringStoredAuthProtector(_keyring, () => _utcNow, TimeSpan.FromMilliseconds(200));
                int start = StartLog("KeyringStoredAuthTests timeout marker");

                Assert.IsFalse(StoredAuth.CanProtect);
                Assert.AreEqual(String.Empty, StoredAuth.Protect(FakePageAuth));

                release.Set();
                Assert.Contains("TimeoutException", ReadLog().Substring(start));
                Assert.AreEqual(0, _keyring.Writes);
            }
        }

        // An unexpected exception from the store never fails a save
        [TestMethod]
        public void AnyExceptionFromTheStoreIsAFailure() {
            _keyring.Failure = new InvalidOperationException("unexpected");
            ThreadInfo thread = ThreadListFile.Parse(Version4Lines(FakePageAuth, "")).Threads[0];

            string[] written = ThreadListFile.Serialize(new[] { thread });

            Assert.AreEqual(String.Empty, written[2]);
            Assert.IsFalse(StoredAuth.CanProtect);
        }

        // No libsecret, no Secret Service running, a missing function: the part-1 behavior
        [TestMethod]
        [DataRow("library")]
        [DataRow("function")]
        [DataRow("store")]
        public void AStoreUnavailableAtFirstUseFallsBackToSessionOnlyLogins(string failure) {
            _keyring.Failure = failure == "library" ? new DllNotFoundException("libsecret-1.so.0")
                : failure == "function" ? (Exception)new EntryPointNotFoundException("secret_password_lookupv_sync") : new KeyringException("GError 2: no daemon");
            string fromBefore = NewToken();
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + fromBefore, "UseSlug=1" });
            Settings.Load(SettingsPath);

            Assert.IsFalse(StoredAuth.CanProtect);
            Assert.AreEqual(String.Empty, StoredAuth.Protect(FakePageAuth));
            Assert.AreEqual(String.Empty, Settings.PageAuth);
            Settings.PageAuth = FakePageAuth;
            Settings.Save(SettingsPath);

            Assert.AreEqual(FakePageAuth, Settings.PageAuth);
            CollectionAssert.AreEqual(new[] { "PageAuth=" + fromBefore, "UseSlug=1" }, File.ReadAllLines(SettingsPath));
            Assert.AreEqual(0, _keyring.Writes);
        }

        private sealed class FakeKeyring : ILoginKeyring {
            public readonly Dictionary<string, string> Items = new Dictionary<string, string>(StringComparer.Ordinal);

            public int Writes { get; private set; }

            public int Clears { get; private set; }

            // Thrown by every call while set
            public Exception Failure { get; set; }

            // Every call waits for it while set
            public ManualResetEventSlim Block { get; set; }

            public string Prefix {
                get { return StoredAuth.KeychainPrefix; }
            }

            public string Name {
                get { return "the fake store"; }
            }

            public void Store(string id, string login) {
                ThrowIfFailing();
                Writes++;
                Items[id] = login;
            }

            public string Lookup(string id) {
                ThrowIfFailing();
                string login;
                return Items.TryGetValue(id, out login) ? login : null;
            }

            public void Clear(string id) {
                ThrowIfFailing();
                Clears++;
                Items.Remove(id);
            }

            private void ThrowIfFailing() {
                if (Block != null) Block.Wait();
                if (Failure != null) throw Failure;
            }
        }
    }
}
