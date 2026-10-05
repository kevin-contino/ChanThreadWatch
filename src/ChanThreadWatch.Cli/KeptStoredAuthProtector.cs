using System;

namespace JDP.Cli {
    // The saved-login backend of the command line, which never reads or writes a login. Every stored value
    // (a DPAPI value, a Keychain or Secret Service reference, or one from another system) reads back as a login
    // that can't be decrypted, so ThreadListFile keeps it and writes it back byte for byte (see
    // StoredAuth.ToStored, which then also schedules no deletion). So ctw never decrypts a login, never asks the
    // system's login store (no Keychain or Secret Service prompt), and never deletes an item from it. A thread
    // that ctw removes leaves its login's item in the store, if it had one.
    internal sealed class KeptStoredAuthProtector : IStoredAuthProtector {
        public bool CanProtect {
            get { return false; }
        }

        // Only a non-empty login is protected, and ctw never sets one: the threads it adds have none, and
        // ThreadListCommands refuses to write a thread list that holds plaintext logins
        public string Protect(string line, string previousStored) {
            throw new InvalidOperationException("ctw never writes a saved login.");
        }

        public string Unprotect(string stored) {
            return String.Empty;
        }

        // Only StoredAuthDeletes deletes, after a save by the app, and ctw never calls it
        public void Delete(string stored) {
            throw new InvalidOperationException("ctw never deletes a saved login.");
        }
    }
}
