using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace JDP {
    // Saved logins ("user:pass") are kept by the system's IStoredAuthProtector. On Windows they
    // are encrypted with DPAPI for the current user, and on disk an encrypted value is "dpapi:"
    // followed by base64. On macOS (Keychain) and Linux (Secret Service) the login is kept in the
    // system's login store, and on disk the value is a reference to it, "keychain:" or
    // "secret-service:" followed by a random id. Where no login store can be used, a login is
    // used for the session only and written as empty. Any other non-empty value is plaintext
    // written by an older version: it is used as-is and replaced on the next save, never written
    // back as plaintext. A value that can't be read (for example a file copied from another
    // user, computer or system, a removed item, or a locked store) is used as an empty login,
    // so it is never sent, but it is kept and written back unchanged until a new login is set.
    public static class StoredAuth {
        public const string Prefix = DpapiStoredAuthProtector.Prefix;
        internal const string KeychainPrefix = "keychain:";
        internal const string SecretServicePrefix = "secret-service:";

        // The random id after KeychainPrefix or SecretServicePrefix: 128 bits in lowercase hex
        internal const int ReferenceIdLength = 32;

        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
        // Values whose items are deleted once no file refers to them (see ScheduleDelete)
        private static readonly HashSet<string> _scheduledDeletes = new HashSet<string>(StringComparer.Ordinal);

        // Tests swap in another backend, for example the unavailable one on Windows
        internal static IStoredAuthProtector Protector { get; set; } = CreateProtector();

        // RuntimeInformation rather than OperatingSystem.IsWindows: tools/stored-auth-check also builds this file for .NET Framework.
        private static IStoredAuthProtector CreateProtector() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new DpapiStoredAuthProtector();
#if NETFRAMEWORK
            return new UnavailableStoredAuthProtector();
#else
            return KeyringStoredAuthProtector.CreateForThisSystem(KeyringStoredAuthProtector.ServiceName) ?? new UnavailableStoredAuthProtector();
#endif
        }

        // False if logins can't be kept on this system, so a login is used for the session only
        public static bool CanProtect {
            get { return Protector.CanProtect; }
        }

        // Every system recognizes every backend's values, so a value from another system is kept, never sent.
        // A reference is recognized only in its exact form, so a login like "keychain:user" stays a login.
        public static bool IsProtected(string stored) {
            if (stored == null) return false;
            return stored.StartsWith(Prefix, StringComparison.Ordinal) || IsReference(stored, KeychainPrefix) || IsReference(stored, SecretServicePrefix);
        }

        // True for the prefix followed by exactly ReferenceIdLength lowercase hex digits
        internal static bool IsReference(string stored, string prefix) {
            return stored != null && stored.Length == prefix.Length + ReferenceIdLength && stored.StartsWith(prefix, StringComparison.Ordinal) &&
                stored.Substring(prefix.Length).TrimStart("0123456789abcdef".ToCharArray()).Length == 0;
        }

        // True if logins are kept in the system's login store, outside the files (Keychain, Secret Service)
        internal static bool KeepsLoginsOutsideTheFiles {
            get { return !(Protector is DpapiStoredAuthProtector) && Protector.CanProtect; }
        }

        // True for a non-empty login that was written unencrypted by an older version
        public static bool IsPlaintext(string stored) {
            return !String.IsNullOrEmpty(stored) && !IsProtected(stored);
        }

        public static string Protect(string auth) {
            return Protect(auth, null);
        }

        // Line breaks are replaced as in the rest of the file, so a login reads back the
        // same whether it was saved encrypted or as plaintext. Null and empty stay empty, and
        // so does a login this system can't keep (see CanProtect). previousStored is the value
        // the login was last read from or written as, see IStoredAuthProtector.Protect.
        public static string Protect(string auth, string previousStored) {
            string line = TextFile.ToSingleLine(auth);
            if (line.Length == 0) return String.Empty;
            return Protector.Protect(line, previousStored);
        }

        // Returns the plaintext login, or an empty string if there is none or it can't be decrypted.
        public static string Unprotect(string stored) {
            if (String.IsNullOrEmpty(stored)) return String.Empty;
            if (!IsProtected(stored)) return stored;
            return Protector.Unprotect(stored);
        }

        // Schedules the removal of the item a stored value refers to (Keychain, Secret Service), for
        // a login that is cleared or replaced or a thread that is removed. The item is deleted only
        // after a save, and only while no file of the settings folder refers to it (see
        // StoredAuthDeletes), so a restored backup keeps its logins and a crash before the save
        // loses nothing. Nothing happens for other values (a DPAPI value holds its login itself).
        public static void ScheduleDelete(string stored) {
            if (!IsReference(stored, KeychainPrefix) && !IsReference(stored, SecretServicePrefix)) return;
            lock (_scheduledDeletes) {
                _scheduledDeletes.Add(stored);
            }
        }

        // Returns the scheduled values and clears the list
        internal static List<string> TakeScheduledDeletes() {
            lock (_scheduledDeletes) {
                List<string> scheduled = new List<string>(_scheduledDeletes);
                _scheduledDeletes.Clear();
                return scheduled;
            }
        }

        // Returns the stored value if it is encrypted but Unprotect gave back nothing, so
        // that it can be written back unchanged; otherwise null.
        public static string GetUndecryptable(string stored, string unprotected) {
            return IsProtected(stored) && String.IsNullOrEmpty(unprotected) ? stored : null;
        }

        // Returns the value to write: a kept undecryptable value while there is no login to
        // write in its place (the login is empty, or this system can't keep it), otherwise
        // the protected login.
        public static string ToStored(string auth, string undecryptable) {
            return ToStored(auth, null, undecryptable);
        }

        // As ToStored, for a login last read from or written as previousStored (null for none).
        // A login that can't be kept now leaves previousStored in place, and the item of a
        // previousStored that is no longer written is scheduled for deletion (a cleared login).
        public static string ToStored(string auth, string previousStored, string undecryptable) {
            string stored = Protect(auth, previousStored);
            if (stored.Length == 0) stored = GetKeptValue(auth, previousStored, undecryptable);
            if (previousStored != null && previousStored != stored) ScheduleDelete(previousStored);
            return stored;
        }

        private static string GetKeptValue(string auth, string previousStored, string undecryptable) {
            if (undecryptable != null) return undecryptable;
            return IsProtected(previousStored) && TextFile.ToSingleLine(auth).Length != 0 ? previousStored : String.Empty;
        }

        // Returns the undecryptable value to keep after a thread's login is edited (null for
        // none). A login set in the edit form, even an empty one, replaces it; on a system that
        // can't keep logins only an empty one does, so a session login never deletes a value
        // from Windows.
        public static string UndecryptableAfterEdit(string undecryptable, string editedAuth) {
            return CanProtect || String.IsNullOrEmpty(editedAuth) ? null : undecryptable;
        }

        // Logs each message once per key (a stored value, or a fixed key) per session, so the
        // periodic backup doesn't repeat it. Never logs a login or a stored value.
        internal static void ReportOnce(string key, string message) {
            lock (_reported) {
                if (!_reported.Add(key)) return;
            }
            Logger.Log(message);
        }
    }
}
