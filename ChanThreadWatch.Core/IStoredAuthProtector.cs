namespace JDP {
    // How saved logins are kept on this system (see StoredAuth): DPAPI on Windows, the Keychain
    // on macOS and the Secret Service (libsecret) on Linux. Where none can be used, a login is
    // used for the session only and never written as plaintext.
    internal interface IStoredAuthProtector {
        // False if this system can't keep a login: Protect then returns an empty string
        bool CanProtect { get; }

        // line is a non-empty login on one line. previousStored is the value this login was last
        // read from or written as (null for none): a backend that keeps logins outside the file
        // reuses its item, so changing a login updates it in place. Returns the value to write,
        // which never holds the login as plaintext, or an empty string if it can't be kept.
        string Protect(string line, string previousStored);

        // stored is a value StoredAuth.IsProtected accepts. Returns the login, or an empty
        // string if it can't be read here.
        string Unprotect(string stored);

        // Removes what a stored value refers to, if this backend keeps it outside the file (a
        // cleared login, a removed thread). Does nothing for a value of another backend.
        void Delete(string stored);
    }
}
