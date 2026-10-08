using System;
using System.Collections.Generic;
using System.Linq;

namespace JDP.Api {
    // How the follow of a pairing code ended
    internal enum ApiPairingEnd {
        Paired,
        // The code's 5 minutes passed, and no browser paired since the code was made
        Expired,
        // The file is gone before the code's expiry (the API burned the code, or it was deleted), and no browser paired
        // since the code was made
        Ended,
        // The file holds another code
        Replaced,
        // The file is there but not trusted (a link, access for others, content that does not parse)
        Changed,
        // The creator stopped (ctw api-pair only: Ctrl+C)
        Cancelled
    }

    // One code and the follow of its result, for a program that makes codes (ctw api-pair, the app's Local API
    // dialog). Each look reads api-pairing.txt and compares its id with the code's, as ApiPairingFile asks of a
    // creator, and End deletes the file only with this code's id. A file that is gone (or still unreadable at the
    // code's expiry), or a pending code past its expiry, may still follow a pairing: the API can save the browser's
    // token and then find the file gone, or a finish can land in the last moment, so the paired browsers are compared
    // with the ones taken just after the code was made, before anyone could know it. A file that holds another code is
    // reported as replaced, although a pairing with this code may have been saved just before (the design accepts
    // both windows).
    internal sealed class ApiPairingFollow {
        private readonly ApiPairingFile _file;
        private readonly ApiClientStore _clients;
        private readonly Func<DateTimeOffset> _utcNow;
        private HashSet<string> _hashesBefore;
        // Set once the code is made; read by a signal handler or the UI thread
        private volatile ApiPairingCode _code;

        public ApiPairingFollow(string settingsFolder, Func<DateTimeOffset> utcNow) {
            _file = new ApiPairingFile(settingsFolder);
            _clients = new ApiClientStore(settingsFolder);
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        // Null until MakeCode returns
        public ApiPairingCode Code {
            get { return _code; }
        }

        // The browser that paired, once a look ended with Paired
        public string PairedFamily { get; private set; }

        // Makes the code (PBKDF2, a moment of work, so a UI calls it off its UI thread), then takes the paired
        // browsers: no browser can know the code yet. Throws ApiTokenException when the file cannot be written with
        // owner-only access.
        public void MakeCode() {
            _code = _file.Create(_utcNow());
            _hashesBefore = PairedHashes();
        }

        // The time left until the code's expiry; less than zero once it expired
        public TimeSpan Remaining {
            get { return Code.Expires - _utcNow(); }
        }

        // One look at the file, once MakeCode returned. Null while the code is pending in the file (or the file can't
        // be read just now).
        public ApiPairingEnd? Look() {
            bool refused, unreadable;
            ApiPendingPairing pending = _file.Read(out refused, out unreadable);
            if (pending == null) return LookWithoutCode(refused, unreadable);
            if (pending.Id != Code.Id) return ApiPairingEnd.Replaced;
            if (pending.PairedFamily != null) return Paired(pending.PairedFamily);
            return IsExpired() ? LookForNewClient() : null;
        }

        private bool IsExpired() {
            return _utcNow() >= Code.Expires;
        }

        // A file that can't be read is waited for until the code's expiry, which then looks for a pairing as for a
        // file that is gone
        private ApiPairingEnd? LookWithoutCode(bool refused, bool unreadable) {
            if (unreadable) return IsExpired() ? LookForNewClient() : null;
            return refused ? ApiPairingEnd.Changed : LookForNewClient();
        }

        // A browser that paired since the code was made is a pairing, else the code ended. Without the paired browsers
        // from then (that file could not be used) no browser counts as new.
        private ApiPairingEnd? LookForNewClient() {
            IReadOnlyList<ApiClient> clients = _clients.Read();
            if (clients == null) return LookWithoutClients();
            ApiClient added = clients.FirstOrDefault(IsNew);
            if (added != null) return Paired(added.Family);
            return IsExpired() ? ApiPairingEnd.Expired : ApiPairingEnd.Ended;
        }

        private bool IsNew(ApiClient client) {
            return _hashesBefore != null && !_hashesBefore.Contains(Convert.ToHexString(client.Hash));
        }

        // api-clients.txt can't be used just now: the follow goes on until the code's expiry
        private ApiPairingEnd? LookWithoutClients() {
            return IsExpired() ? ApiPairingEnd.Expired : null;
        }

        private ApiPairingEnd Paired(string family) {
            PairedFamily = family;
            return ApiPairingEnd.Paired;
        }

        // Null when api-clients.txt can't be used
        private HashSet<string> PairedHashes() {
            IReadOnlyList<ApiClient> clients = _clients.Read();
            return clients != null ? new HashSet<string>(clients.Select(client => Convert.ToHexString(client.Hash)), StringComparer.Ordinal) : null;
        }

        // Deletes the file only while it holds this code; true when there is no code yet or the file is gone or holds
        // another code, false when it could not be deleted
        public bool End() {
            ApiPairingCode code = _code;
            return code == null || _file.Delete(code.Id);
        }

        public static string DisplayName(string family) {
            return family == ApiPairing.ChromeFamily ? "Chrome" : "Firefox";
        }

        // How the code ended, in words. again is the advice for a new code (with a leading space); otherCreators names
        // the other programs that make codes.
        public static string DescribeEnd(ApiPairingEnd end, string family, string again, string otherCreators) {
            switch (end) {
                case ApiPairingEnd.Paired:
                    return "Paired with the " + DisplayName(family) + " extension";
                case ApiPairingEnd.Expired:
                    return "The code expired before a browser extension paired with it." + again;
                case ApiPairingEnd.Ended:
                    return "The code no longer works, and no browser extension paired with it: wrong codes used up its tries, or " + ApiPairingFile.FileName +
                        " was deleted." + again;
                default:
                    return DescribeOtherEnd(end, again, otherCreators);
            }
        }

        private static string DescribeOtherEnd(ApiPairingEnd end, string again, string otherCreators) {
            if (end == ApiPairingEnd.Replaced) return "A newer code from another program that makes codes, such as " + otherCreators + ", replaced this one. Use the newer code.";
            if (end == ApiPairingEnd.Changed) return ApiPairingFile.FileName + " was changed by another program, so the code no longer works." + again;
            return "Cancelled. The code no longer works.";
        }
    }
}
