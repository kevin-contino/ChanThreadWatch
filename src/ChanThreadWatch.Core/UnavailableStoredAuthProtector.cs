using System;

namespace JDP {
    // Systems that can't keep logins: no backend for the system, or its login store can't be
    // used (see KeyringStoredAuthProtector). A login is never written: it is used for the
    // session only, and a stored value from another system (e.g. a settings folder copied
    // from Windows) is not used but kept in the file. Each case is logged once, and nothing
    // here throws, so a save never fails because of a login.
    internal sealed class UnavailableStoredAuthProtector : IStoredAuthProtector {
        // Not a stored value (those start with a prefix), so it can't collide with one
        private const string NotKeptReportKey = "*not kept*";

        public bool CanProtect {
            get { return false; }
        }

        public string Protect(string line, string previousStored) {
            StoredAuth.ReportOnce(NotKeptReportKey, "Saved logins can't be kept on this system, so a login is used for this session only " +
                "and is not written to the settings or the thread list.");
            return String.Empty;
        }

        public string Unprotect(string stored) {
            StoredAuth.ReportOnce(stored, "A saved login could not be decrypted on this system (it was saved on another system, " +
                "or this system's login store can't be used), so it is not used. It is kept in the file until a new login is set.");
            return String.Empty;
        }

        public void Delete(string stored) {
        }
    }
}
