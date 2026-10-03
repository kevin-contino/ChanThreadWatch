using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace JDP {
    // Saved logins ("user:pass") are encrypted with DPAPI for the current Windows user. On
    // disk an encrypted value is "dpapi:" followed by base64. Any other non-empty value is
    // plaintext written by an older version: it is used as-is and encrypted on the next save.
    // A value that can't be decrypted (for example a file copied from another user or
    // computer, or a temporary DPAPI failure) is used as an empty login, so it is never sent,
    // but it is kept and written back unchanged until a new login is set.
    public static class StoredAuth {
        public const string Prefix = "dpapi:";

        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("JDP.ChanThreadWatch.StoredAuth.v1");
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public static bool IsProtected(string stored) {
            return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
        }

        // Line breaks are replaced as in the rest of the file, so a login reads back the
        // same whether it was saved encrypted or as plaintext. Null and empty stay empty.
        public static string Protect(string auth) {
            string line = TextFile.ToSingleLine(auth);
            if (line.Length == 0) return String.Empty;
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(line), _entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        // Returns the plaintext login, or an empty string if there is none or it can't be decrypted.
        public static string Unprotect(string stored) {
            if (String.IsNullOrEmpty(stored)) return String.Empty;
            if (!IsProtected(stored)) return stored;
            return TryDecrypt(stored.Substring(Prefix.Length));
        }

        // Returns the stored value if it is encrypted but Unprotect gave back nothing, so
        // that it can be written back unchanged; otherwise null.
        public static string GetUndecryptable(string stored, string unprotected) {
            return IsProtected(stored) && String.IsNullOrEmpty(unprotected) ? stored : null;
        }

        // Returns the value to write: a kept undecryptable value while the login is still
        // empty, otherwise the encrypted login.
        public static string ToStored(string auth, string undecryptable) {
            return undecryptable != null && String.IsNullOrEmpty(auth) ? undecryptable : Protect(auth);
        }

        private static string TryDecrypt(string base64) {
            try {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(base64), _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException) {
                ReportOnce(base64, ex);
                return String.Empty;
            }
        }

        // Logs each undecryptable value once per session, so the periodic backup doesn't
        // repeat it. Never logs the value itself, encrypted or not.
        private static void ReportOnce(string base64, Exception ex) {
            lock (_reported) {
                if (!_reported.Add(base64)) return;
            }
            Logger.Log("A saved login could not be decrypted (" + ex.GetType().Name + "), so it is not used. " +
                "It is kept in the file until a new login is set. Logins saved by another Windows user or on another computer can't be read.");
        }
    }
}
