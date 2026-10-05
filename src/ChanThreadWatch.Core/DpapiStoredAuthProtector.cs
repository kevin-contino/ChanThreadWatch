using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace JDP {
    // Windows: a login is encrypted with DPAPI for the current user, and written as "dpapi:"
    // followed by base64. This is the format 1.39 and later write; it must not change, or
    // logins saved by those versions (and by the .NET Framework 4.8 app) can't be read.
    // The login is in the value itself, so there is no item to reuse or delete.
    internal sealed class DpapiStoredAuthProtector : IStoredAuthProtector {
        public const string Prefix = "dpapi:";

        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("JDP.ChanThreadWatch.StoredAuth.v1");

        public bool CanProtect {
            get { return true; }
        }

        public string Protect(string line, string previousStored) {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw NotSupported();
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(line), _entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        public string Unprotect(string stored) {
            return stored.StartsWith(Prefix, StringComparison.Ordinal) ? Decrypt(stored) : ReportOtherSystem(stored);
        }

        private static string Decrypt(string stored) {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw NotSupported();
            try {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException) {
                StoredAuth.ReportOnce(stored, "A saved login could not be decrypted (" + ex.GetType().Name + "), so it is not used. " +
                    "It is kept in the file until a new login is set. Logins saved by another Windows user or on another computer can't be read.");
                return String.Empty;
            }
        }

        public void Delete(string stored) {
        }

        // A Keychain or Secret Service reference from macOS or Linux
        private static string ReportOtherSystem(string stored) {
            StoredAuth.ReportOnce(stored, "A saved login could not be decrypted on this system (it was saved on macOS or Linux), so it is not used. " +
                "It is kept in the file until a new login is set.");
            return String.Empty;
        }

        // DPAPI exists only on Windows, and StoredAuth picks this class only there.
        // RuntimeInformation rather than OperatingSystem.IsWindows: tools/stored-auth-check also builds this file for .NET Framework.
        private static PlatformNotSupportedException NotSupported() {
            return new PlatformNotSupportedException("Saved logins can only be encrypted and decrypted with DPAPI on Windows.");
        }
    }
}
