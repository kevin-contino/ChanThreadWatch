using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace JDP {
    // A system login store that keeps each login as an item named by an id (see MacKeychain and
    // LibSecretKeyring). Lookup returns null for a missing item; every method throws (for example
    // KeyringException, or a TypeLoadException for a missing library or function) when the store
    // can't be used, for example when it is locked or not running.
    internal interface ILoginKeyring {
        // The start of the stored values that refer to this store's items
        string Prefix { get; }

        // The store's name in log messages
        string Name { get; }

        void Store(string id, string login);

        string Lookup(string id);

        // Removes the item; a missing item is not an error
        void Clear(string id);
    }

    internal sealed class KeyringException : Exception {
        public KeyringException(string message) : base(message) {
        }
    }

    // macOS and Linux: a login is kept in the system's login store, and the file holds only a
    // reference to its item: the store's prefix followed by a random 128-bit id in hex. A login
    // that changes keeps its id, so its item is updated in place. Items of cleared logins and
    // removed threads are deleted after the next save (see StoredAuth.ScheduleDelete). Items are
    // never swept at startup, since backups, copies kept aside and other settings folders can
    // refer to them too.
    // Each call to the store runs on a worker thread and counts as failed after CallTimeout: the
    // store can show a prompt that waits for the user (a macOS "allow access" dialog for an item
    // another build of the app wrote, or a Linux keyring unlock prompt). While the store fails
    // (missing library, no Secret Service running, a locked keychain, a timeout), logins are used
    // for the session only (as UnavailableStoredAuthProtector) and values already in the files are
    // kept; the store is tried again after RetryAfter. The first failure and the recovery are
    // each logged once.
    internal sealed class KeyringStoredAuthProtector : IStoredAuthProtector {
        public const string ServiceName = "Chan Thread Watch";
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

        private readonly ILoginKeyring _keyring;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _callTimeout;
        private readonly UnavailableStoredAuthProtector _unavailable = new UnavailableStoredAuthProtector();
        private readonly object _sync = new object();
        // The login of each value read or written this session
        private readonly Dictionary<string, string> _known = new Dictionary<string, string>(StringComparer.Ordinal);
        private DateTime _unavailableUntil = DateTime.MinValue;
        // The store answered since the last failure
        private bool _answered;
        // A failure was logged and the store hasn't answered since
        private bool _failing;

        public KeyringStoredAuthProtector(ILoginKeyring keyring) : this(keyring, () => DateTime.UtcNow, CallTimeout) {
        }

        // Tests pass their own clock and timeout
        internal KeyringStoredAuthProtector(ILoginKeyring keyring, Func<DateTime> utcNow, TimeSpan callTimeout) {
            if (keyring == null) throw new ArgumentNullException("keyring");
            if (utcNow == null) throw new ArgumentNullException("utcNow");
            _keyring = keyring;
            _utcNow = utcNow;
            _callTimeout = callTimeout;
        }

        // Returns null on a system without a login store backend
        public static IStoredAuthProtector CreateForThisSystem(string service) {
            if (OperatingSystem.IsMacOS()) return new KeyringStoredAuthProtector(new MacKeychain(service));
            if (OperatingSystem.IsLinux()) return new KeyringStoredAuthProtector(new LibSecretKeyring(service));
            return null;
        }

        public bool CanProtect {
            get { lock (_sync) { return IsAvailable(); } }
        }

        public string Protect(string line, string previousStored) {
            lock (_sync) {
                string stored = IsAvailable() ? Write(line, previousStored) : null;
                return stored ?? _unavailable.Protect(line, previousStored);
            }
        }

        public string Unprotect(string stored) {
            lock (_sync) {
                string id = GetOwnId(stored);
                if (id == null) return _unavailable.Unprotect(stored);
                if (!IsAvailable()) return ReportUnreadable(stored);
                string login;
                return _known.TryGetValue(stored, out login) ? login : Read(stored, id);
            }
        }

        // A delete the store can't do now is scheduled again, for the next save
        public void Delete(string stored) {
            lock (_sync) {
                string id = GetOwnId(stored);
                if (id == null) return;
                _known.Remove(stored);
                if (!IsAvailable() || !TryUse(() => _keyring.Clear(id))) StoredAuth.ScheduleDelete(stored);
            }
        }

        // Returns null if the store failed
        private string Write(string line, string previousStored) {
            string stored = _keyring.Prefix + (GetOwnId(previousStored) ?? NewId());
            if (IsUnchanged(stored, line)) return stored;
            return IsAvailable() && Store(stored, line) ? stored : null;
        }

        // A login read or written this session is checked in the store before its write is
        // skipped, so an item deleted outside the app (e.g. in Keychain Access) is written again.
        // A lookup is cheaper than a write, which can also ask the user for access on macOS.
        private bool IsUnchanged(string stored, string line) {
            string login;
            if (!_known.TryGetValue(stored, out login) || login != line) return false;
            string current = null;
            return TryUse(() => current = _keyring.Lookup(GetOwnId(stored))) && current == line;
        }

        private bool Store(string stored, string line) {
            if (!TryUse(() => _keyring.Store(GetOwnId(stored), line))) return false;
            _known[stored] = line;
            return true;
        }

        private string Read(string stored, string id) {
            string login = null;
            if (!TryUse(() => login = _keyring.Lookup(id))) return ReportUnreadable(stored);
            if (login == null) return ReportMissing(stored);
            _known[stored] = login;
            return login;
        }

        // After a failure, the store is checked again with a lookup of an id that doesn't exist
        private bool IsAvailable() {
            if (_utcNow() < _unavailableUntil) return false;
            if (!_answered) _answered = TryUse(() => _keyring.Lookup(NewId()));
            return _answered;
        }

        private bool TryUse(Action action) {
            Exception failure = Run(action, _callTimeout);
            if (failure == null) {
                ReportRecovered();
                return true;
            }
            _answered = false;
            _unavailableUntil = _utcNow() + RetryAfter;
            ReportFailure(failure);
            return false;
        }

        // Returns the call's exception, a TimeoutException, or null if it succeeded. A call that
        // times out keeps running on its worker; what it returns later is ignored.
        private static Exception Run(Action action, TimeSpan timeout) {
            try {
                return Task.Run(action).Wait(timeout) ? null : new TimeoutException("No answer within " +
                    timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
            }
            catch (AggregateException ex) when (!(ex.InnerException is OutOfMemoryException)) {
                return ex.InnerException ?? ex;
            }
        }

        private void ReportFailure(Exception ex) {
            if (_failing) return;
            _failing = true;
            Logger.Log(_keyring.Name + " can't be used (" + ex.GetType().Name + ": " + ex.Message + "), so logins are used for this " +
                "session only and saved logins can't be read; it is tried again in " + RetryAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture) +
                " minutes. Saved logins are kept in the files.");
        }

        private void ReportRecovered() {
            if (!_failing) return;
            _failing = false;
            Logger.Log(_keyring.Name + " can be used again, so logins are saved there again.");
        }

        private string ReportMissing(string stored) {
            StoredAuth.ReportOnce(stored, "A saved login was not found in " + _keyring.Name + " (it was removed there, or saved by another user " +
                "or computer), so it is not used. It is kept in the file until a new login is set.");
            return String.Empty;
        }

        private string ReportUnreadable(string stored) {
            StoredAuth.ReportOnce(stored, "A saved login could not be read because " + _keyring.Name + " is locked or can't be used, " +
                "so it is not used. It is kept in the file until a new login is set.");
            return String.Empty;
        }

        // Returns null for a value of another store or a malformed one
        private string GetOwnId(string stored) {
            return StoredAuth.IsReference(stored, _keyring.Prefix) ? stored.Substring(_keyring.Prefix.Length) : null;
        }

        private static string NewId() {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(StoredAuth.ReferenceIdLength / 2)).ToLowerInvariant();
        }
    }
}
