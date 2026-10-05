using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // ctw never decrypts, writes or deletes a saved login: the stored values of the threads it does not add or
    // remove are written back byte for byte, whatever the system. The values here are made up, so no DPAPI,
    // Keychain or Secret Service is used (ctw's backend never calls one).
    [TestClass]
    public class SavedLoginTests : CliTestBase {
        private const string DpapiValue = "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAzmd2FnUeb0+Pfl1XzxV1AAAAAAACAAAAAAAQZgAAAAEAACAAAAA=";
        private const string KeychainValue = "keychain:0123456789abcdef0123456789abcdef";
        private const string SecretServiceValue = "secret-service:fedcba9876543210fedcba9876543210";
        private const string OtherKeychainValue = "keychain:aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbb";

        [TestCleanup]
        public void ClearScheduledDeletes() {
            StoredAuth.TakeScheduledDeletes();
        }

        private void WriteThreadsWithLogins() {
            WriteThreadList(
                ThreadLines("https://boards.4chan.org/a/thread/1", pageAuth: DpapiValue, imageAuth: KeychainValue),
                ThreadLines("https://boards.4chan.org/a/thread/2", pageAuth: SecretServiceValue),
                ThreadLines("https://boards.4chan.org/a/thread/3", imageAuth: OtherKeychainValue));
        }

        [TestMethod]
        public void Add_KeepsEveryOtherThreadByteForByte() {
            WriteThreadsWithLogins();
            byte[] before = File.ReadAllBytes(ThreadListPath);

            AssertSucceeded(Run("add", "https://boards.4chan.org/b/thread/4"));

            byte[] after = File.ReadAllBytes(ThreadListPath);
            Assert.IsGreaterThan(before.Length, after.Length);
            CollectionAssert.AreEqual(before, after[..before.Length]);
            Assert.HasCount(0, StoredAuth.TakeScheduledDeletes());
        }

        // Removing a thread whose login is in the login store leaves that item alone (the app deletes it after
        // its own save; ctw never does), and the other threads keep their values
        [TestMethod]
        public void Remove_KeepsTheOtherLoginsAndSchedulesNoDeletion() {
            WriteThreadsWithLogins();

            CliResult result = Run("remove", "https://boards.4chan.org/a/thread/3");

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("ctw: note: " + StoredLogins.KeptItemNote + Environment.NewLine, result.Error);
            string[] lines = File.ReadAllLines(ThreadListPath);
            CollectionAssert.AreEqual(new[] { DpapiValue, KeychainValue }, new[] { lines[2], lines[3] });
            CollectionAssert.AreEqual(new[] { SecretServiceValue, "" }, new[] { lines[15], lines[16] });
            Assert.HasCount(27, lines);
            Assert.HasCount(0, StoredAuth.TakeScheduledDeletes());
            StringAssert.DoesNotMatch(File.ReadAllText(ThreadListPath), new System.Text.RegularExpressions.Regex(OtherKeychainValue));
        }

        [TestMethod]
        public void ListAndAddAndRemove_NeverAskTheLoginBackend() {
            WriteThreadsWithLogins();
            StrictProtector strict = new StrictProtector();
            StoredAuth.Protector = strict;
            try {
                // ctw puts its own backend in place before it reads the file
                AssertSucceeded(Run("list"));
                AssertSucceeded(Run("add", "https://boards.4chan.org/b/thread/4"));
                Assert.AreEqual(CliApp.ExitSuccess, Run("remove", "https://boards.4chan.org/a/thread/2").ExitCode);
            }
            finally {
                StoredAuth.Protector = new KeptStoredAuthProtector();
            }
            Assert.AreEqual(0, strict.Calls);
        }

        // A DPAPI value holds the login itself, so nothing stays behind in a login store
        [TestMethod]
        [DataRow("https://boards.4chan.org/a/thread/2", true)]
        [DataRow("https://boards.4chan.org/b/thread/9", false)]
        public void Remove_NotesALoginKeptInTheLoginStoreOnly(string url, bool expectNote) {
            WriteThreadList(
                ThreadLines("https://boards.4chan.org/a/thread/2", pageAuth: SecretServiceValue),
                ThreadLines("https://boards.4chan.org/b/thread/9", pageAuth: DpapiValue));

            CliResult result = Run("remove", url);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(expectNote ? "ctw: note: " + StoredLogins.KeptItemNote + Environment.NewLine : String.Empty, result.Error);
        }

        [TestMethod]
        public void List_DoesNotShowLogins() {
            WriteThreadsWithLogins();

            CliResult result = Run("list");

            AssertSucceeded(result);
            Assert.HasCount(3, Lines(result.Output));
            StringAssert.DoesNotMatch(result.Output, new System.Text.RegularExpressions.Regex("dpapi:|keychain:|secret-service:"));
        }

        // A plaintext login from an older version can be neither written back (it must not stay plaintext) nor
        // encrypted by ctw (it never writes a login), so the file is left for the app to encrypt
        [TestMethod]
        public void PlaintextLogin_ListsButRefusesChanges() {
            WriteThreadList(ThreadLines("https://boards.4chan.org/a/thread/1", pageAuth: "user:secret"));
            byte[] before = File.ReadAllBytes(ThreadListPath);

            CliResult list = Run("list");
            AssertSucceeded(list);
            Assert.IsFalse(list.Output.Contains("secret", StringComparison.Ordinal));

            AssertFailed(Run("add", "https://boards.4chan.org/b/thread/4"), CliApp.ExitFailure, "holds logins saved without encryption");
            AssertFailed(Run("remove", "https://boards.4chan.org/a/thread/1"), CliApp.ExitFailure, "holds logins saved without encryption");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        [TestMethod]
        public void KeptStoredAuthProtector_NeverWritesOrDeletesALogin() {
            KeptStoredAuthProtector protector = new KeptStoredAuthProtector();

            Assert.IsFalse(protector.CanProtect);
            Assert.AreEqual(String.Empty, protector.Unprotect(DpapiValue));
            Assert.AreEqual(String.Empty, protector.Unprotect(KeychainValue));
            Assert.ThrowsExactly<InvalidOperationException>(() => protector.Protect("user:pass", null));
            Assert.ThrowsExactly<InvalidOperationException>(() => protector.Delete(KeychainValue));
        }

        // Fails the test if anything asks it
        private sealed class StrictProtector : IStoredAuthProtector {
            public int Calls;

            public bool CanProtect {
                get { Calls++; return false; }
            }

            public string Protect(string line, string previousStored) {
                Calls++;
                return String.Empty;
            }

            public string Unprotect(string stored) {
                Calls++;
                return String.Empty;
            }

            public void Delete(string stored) {
                Calls++;
            }
        }
    }
}
