using System;

namespace JDP {
    // Systems without a way to keep logins yet (macOS and Linux until the Keychain and
    // libsecret backends, see MP-4c gate G2). A login is never written: it is used for the
    // session only, and an encrypted value from another system (e.g. a settings folder
    // copied from Windows) is not used but kept in the file. Each case is logged once, and
    // nothing here throws, so a save never fails because of a login.
    internal sealed class UnavailableStoredAuthProtector : IStoredAuthProtector {
        // Not a stored value (those start with a prefix), so it can't collide with one
        private const string NotKeptReportKey = "*not kept*";

        public bool CanProtect {
            get { return false; }
        }

        public string Protect(string line) {
            StoredAuth.ReportOnce(NotKeptReportKey, "Saved logins can't be encrypted on this system yet, so a login is used for this session only " +
                "and is not written to the settings or the thread list.");
            return String.Empty;
        }

        public string Unprotect(string stored) {
            StoredAuth.ReportOnce(stored, "A saved login could not be decrypted on this system (it was encrypted on Windows), so it is not used. " +
                "It is kept in the file until a new login is set.");
            return String.Empty;
        }
    }
}
