using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace JDP {
    // Saved logins ("user:pass") are kept by the system's IStoredAuthProtector: on Windows they
    // are encrypted with DPAPI for the current user, and on disk an encrypted value is "dpapi:"
    // followed by base64. Other systems can't keep a login yet, so it is used for the session
    // only and written as empty. Any other non-empty value is plaintext written by an older
    // version: it is used as-is and replaced on the next save, never written back as plaintext.
    // A value that can't be decrypted (for example a file copied from another user or
    // computer, or a temporary DPAPI failure) is used as an empty login, so it is never sent,
    // but it is kept and written back unchanged until a new login is set.
    public static class StoredAuth {
        public const string Prefix = DpapiStoredAuthProtector.Prefix;

        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        // Tests swap in another backend, for example the unavailable one on Windows
        internal static IStoredAuthProtector Protector { get; set; } = CreateProtector();

        // RuntimeInformation rather than OperatingSystem.IsWindows: tools/stored-auth-check also builds this file for .NET Framework.
        private static IStoredAuthProtector CreateProtector() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new DpapiStoredAuthProtector();
            return new UnavailableStoredAuthProtector();
        }

        // False if logins can't be kept on this system, so a login is used for the session only
        public static bool CanProtect {
            get { return Protector.CanProtect; }
        }

        public static bool IsProtected(string stored) {
            return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
        }

        // True for a non-empty login that was written unencrypted by an older version
        public static bool IsPlaintext(string stored) {
            return !String.IsNullOrEmpty(stored) && !IsProtected(stored);
        }

        // Line breaks are replaced as in the rest of the file, so a login reads back the
        // same whether it was saved encrypted or as plaintext. Null and empty stay empty, and
        // so does a login this system can't keep (see CanProtect).
        public static string Protect(string auth) {
            string line = TextFile.ToSingleLine(auth);
            if (line.Length == 0) return String.Empty;
            return Protector.Protect(line);
        }

        // Returns the plaintext login, or an empty string if there is none or it can't be decrypted.
        public static string Unprotect(string stored) {
            if (String.IsNullOrEmpty(stored)) return String.Empty;
            if (!IsProtected(stored)) return stored;
            return Protector.Unprotect(stored);
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
            string stored = Protect(auth);
            return undecryptable != null && stored.Length == 0 ? undecryptable : stored;
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
