using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The real login stores: the macOS Keychain and the Secret Service through libsecret. They
    // run only in CI, where the job provides a throwaway store and sets CTW_KEYRING_TESTS (see
    // TestLoginStore), so a local run never touches a real store. Every item uses the run's
    // unique test service name and a fresh id, and is deleted at the end. Logins are fake.
    [TestClass]
    public class KeyringBackendTests {
        private static ILoginKeyring CreateKeyring() {
            if (OperatingSystem.IsMacOS()) return new MacKeychain(TestLoginStore.ServiceName);
            return new LibSecretKeyring(TestLoginStore.ServiceName);
        }

        private static string NewId() {
            return Guid.NewGuid().ToString("N");
        }

        // The fallback step of the Linux job runs only the fallback test, with the other value
        private static void RequireTestStore() {
            if (!TestLoginStore.IsEnabled) {
                TestLoginStore.Skip(TestLoginStore.Value != TestLoginStore.FallbackValue, "Runs in CI with " + TestLoginStore.EnvironmentVariable + "=" +
                    TestLoginStore.StoreValue + " and a test keychain or Secret Service.");
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX | OperatingSystems.Linux)]
        public void AnItemIsStoredReadUpdatedAndCleared() {
            RequireTestStore();
            ILoginKeyring keyring = CreateKeyring();
            string id = NewId();
            try {
                Assert.IsNull(keyring.Lookup(id));

                keyring.Store(id, "fakeuser:fakepäss:日本");
                Assert.AreEqual("fakeuser:fakepäss:日本", keyring.Lookup(id));

                keyring.Store(id, "fakeuser:fakenewpass");
                Assert.AreEqual("fakeuser:fakenewpass", keyring.Lookup(id));

                keyring.Clear(id);
                Assert.IsNull(keyring.Lookup(id));
                // Clearing a missing item is not an error
                keyring.Clear(id);
            }
            finally {
                keyring.Clear(id);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX | OperatingSystems.Linux)]
        public void ItemsOfAnotherServiceAreNotSeen() {
            RequireTestStore();
            ILoginKeyring keyring = CreateKeyring();
            ILoginKeyring otherService = OperatingSystem.IsMacOS() ? new MacKeychain(TestLoginStore.ServiceName + " other")
                : (ILoginKeyring)new LibSecretKeyring(TestLoginStore.ServiceName + " other");
            string id = NewId();
            try {
                keyring.Store(id, "fakeuser:fakepass");

                Assert.IsNull(otherService.Lookup(id));
                otherService.Clear(id);
                Assert.AreEqual("fakeuser:fakepass", keyring.Lookup(id));
            }
            finally {
                keyring.Clear(id);
            }
        }

        // A second protector has no session cache, so this reads the item back from the store
        [TestMethod]
        [OSCondition(OperatingSystems.OSX | OperatingSystems.Linux)]
        public void TheProtectorRoundTripsThroughTheStore() {
            RequireTestStore();
            IStoredAuthProtector writer = KeyringStoredAuthProtector.CreateForThisSystem(TestLoginStore.ServiceName);
            Assert.IsTrue(writer.CanProtect);
            string stored = writer.Protect("fakeuser:fakepass", null);
            try {
                Assert.IsTrue(StoredAuth.IsProtected(stored));
                Assert.DoesNotContain("fake", stored);
                IStoredAuthProtector reader = KeyringStoredAuthProtector.CreateForThisSystem(TestLoginStore.ServiceName);
                Assert.AreEqual("fakeuser:fakepass", reader.Unprotect(stored));

                Assert.AreEqual(stored, writer.Protect("fakeuser:fakenewpass", stored));
                Assert.AreEqual("fakeuser:fakenewpass", KeyringStoredAuthProtector.CreateForThisSystem(TestLoginStore.ServiceName).Unprotect(stored));

                writer.Delete(stored);
                Assert.AreEqual(String.Empty, KeyringStoredAuthProtector.CreateForThisSystem(TestLoginStore.ServiceName).Unprotect(stored));
            }
            finally {
                writer.Delete(stored);
            }
        }

        // CI runs this alone with CTW_KEYRING_TESTS=unavailable and no reachable D-Bus session:
        // the real Secret Service backend must fall back to session-only logins, without throwing
        [TestMethod]
        [TestCategory("KeyringFallback")]
        [OSCondition(OperatingSystems.Linux)]
        public void WithoutASecretServiceLoginsAreForTheSessionOnly() {
            if (!TestLoginStore.IsCI || TestLoginStore.Value != TestLoginStore.FallbackValue) {
                // The main test step runs with the store value; only a missing value is a broken job
                TestLoginStore.Skip(TestLoginStore.Value != TestLoginStore.StoreValue, "Runs in CI with " + TestLoginStore.EnvironmentVariable + "=" +
                    TestLoginStore.FallbackValue + " and no D-Bus session.");
            }
            IStoredAuthProtector protector = KeyringStoredAuthProtector.CreateForThisSystem(TestLoginStore.ServiceName);
            string kept = StoredAuth.SecretServicePrefix + NewId();

            Assert.IsFalse(protector.CanProtect);
            Assert.AreEqual(String.Empty, protector.Protect("fakeuser:fakepass", null));
            Assert.AreEqual(String.Empty, protector.Unprotect(kept));
            protector.Delete(kept);
        }
    }
}
