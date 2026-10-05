namespace JDP {
    // How saved logins are kept on this system (see StoredAuth): DPAPI on Windows; elsewhere
    // nothing yet, so a login is used for the session only and never written as plaintext.
    internal interface IStoredAuthProtector {
        // False if this system can't keep a login: Protect then returns an empty string
        bool CanProtect { get; }

        // line is a non-empty login on one line. Returns the value to write, which never
        // holds the login as plaintext, or an empty string if it can't be kept.
        string Protect(string line);

        // stored is a value StoredAuth.IsProtected accepts. Returns the login, or an empty
        // string if it can't be read here.
        string Unprotect(string stored);
    }
}
