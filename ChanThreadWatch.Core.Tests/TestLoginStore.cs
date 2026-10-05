using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The saved-login backend for the whole test run. Windows keeps DPAPI (for the current
    // user; nothing outside the test files). On macOS and Linux the tests never use the real
    // login store under the app's service name. Only in CI (GITHUB_ACTIONS=true) with
    // CTW_KEYRING_TESTS=1, where the job provides a throwaway keychain or Secret Service, do they
    // use the system's store, under a unique test service name, and delete every item they wrote.
    // Everywhere else (a local run, even with the variable set) they use the unavailable backend,
    // and the tests that need a store are inconclusive; in CI a missing value fails them instead.
    [TestClass]
    public static class TestLoginStore {
        public const string EnvironmentVariable = "CTW_KEYRING_TESTS";
        public const string StoreValue = "1";
        public const string FallbackValue = "unavailable";
        public static readonly string ServiceName = "Chan Thread Watch Tests " + Guid.NewGuid().ToString("N");

        private static TrackingProtector _tracking;

        [AssemblyInitialize]
        public static void UseTestLoginStore(TestContext context) {
            // Not about logins, but MSTest allows one AssemblyInitialize per assembly
            TestDefaultFolders.Redirect();
            if (OperatingSystem.IsWindows()) return;
            IStoredAuthProtector keyring = IsEnabled ? KeyringStoredAuthProtector.CreateForThisSystem(ServiceName) : null;
            if (keyring == null) {
                StoredAuth.Protector = new UnavailableStoredAuthProtector();
                return;
            }
            _tracking = new TrackingProtector(keyring);
            StoredAuth.Protector = _tracking;
        }

        [AssemblyCleanup]
        public static void DeleteTestItems() {
            if (_tracking != null) _tracking.DeleteAll();
            TestDefaultFolders.Delete();
        }

        public static bool IsCI {
            get { return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"; }
        }

        public static string Value {
            get { return Environment.GetEnvironmentVariable(EnvironmentVariable); }
        }

        // True when CI provides a login store for the tests (see the class comment)
        public static bool IsEnabled {
            get { return IsCI && Value == StoreValue; }
        }

        // For a test that needs saved logins to read back: DPAPI on Windows, or the test login store
        public static void RequireLoginStore() {
            if (!OperatingSystem.IsWindows() && !IsEnabled) {
                Skip(Value == null, "Needs a login store off Windows: CI sets " + EnvironmentVariable + "=" + StoreValue + " with a test keychain or Secret Service.");
            }
        }

        // In CI a test whose variable is missing fails, so a broken job setup can't pass as skipped
        public static void Skip(bool isMissing, string message) {
            if (IsCI && isMissing) Assert.Fail(EnvironmentVariable + " is not set in CI. " + message);
            Assert.Inconclusive(message);
        }

        // Records every value the backend writes, so the run can delete the items it made
        private sealed class TrackingProtector : IStoredAuthProtector {
            private readonly IStoredAuthProtector _inner;
            private readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);

            public TrackingProtector(IStoredAuthProtector inner) {
                _inner = inner;
            }

            public bool CanProtect {
                get { return _inner.CanProtect; }
            }

            public string Protect(string line, string previousStored) {
                string stored = _inner.Protect(line, previousStored);
                lock (_written) _written.Add(stored);
                return stored;
            }

            public string Unprotect(string stored) {
                return _inner.Unprotect(stored);
            }

            public void Delete(string stored) {
                _inner.Delete(stored);
            }

            public void DeleteAll() {
                lock (_written) {
                    foreach (string stored in _written) _inner.Delete(stored);
                    _written.Clear();
                }
            }
        }
    }
}
