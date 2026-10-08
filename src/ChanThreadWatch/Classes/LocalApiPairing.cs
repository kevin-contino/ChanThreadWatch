using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JDP.Api;

namespace JDP {
    // One code made by the Local API dialog's "Pair extension...", and the follow of its result (ApiPairingFollow, as
    // ctw api-pair follows its code). The code is made off the UI thread (PBKDF2); the dialog can end it meanwhile (it
    // closed, or the API stopped), and a code ended that way is deleted as soon as it is made. Also the dialog's texts.
    internal sealed class LocalApiPairing {
        private const string Again = " Click \"Pair extension...\" for a new code.";
        private const string OtherCreators = "'ctw api-pair'";

        private readonly ApiPairingFollow _follow;
        // Taken to mark the code made and to end it
        private readonly object _sync = new object();
        private bool _made;
        private bool _ended;

        public LocalApiPairing(string settingsFolder, Func<DateTimeOffset> utcNow) {
            _follow = new ApiPairingFollow(settingsFolder, utcNow);
        }

        // Null until MakeCode returns
        public ApiPairingCode Code {
            get { return _follow.Code; }
        }

        // The browser that paired, once a look ended with Paired
        public string PairedFamily {
            get { return _follow.PairedFamily; }
        }

        // The time left until the code's expiry; less than zero once it expired
        public TimeSpan Remaining {
            get { return _follow.Remaining; }
        }

        // On the thread pool. Throws ApiTokenException when the file cannot be written with owner-only access.
        public void MakeCode() {
            _follow.MakeCode();
            lock (_sync) {
                _made = true;
                if (_ended) Delete();
            }
        }

        // One look at the file, once MakeCode returned; null while the code is pending
        public ApiPairingEnd? Look() {
            return _follow.Look();
        }

        // Ends the code: its file is deleted only while it holds this code. A code that is still being made is
        // deleted once it is made. Called on the UI thread, also more than once.
        public void End() {
            lock (_sync) {
                _ended = true;
                if (_made) Delete();
            }
        }

        // A file that can't be deleted leaves a code that a browser can still use until it expires; the dialog may be
        // gone by then, so the log says so
        private void Delete() {
            if (_follow.End()) return;
            Logger.Log("Local API: " + ApiPairingFile.FileName + " could not be deleted, so the pairing code still works until " +
                Code.Expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ".");
        }

        // The time left as m:ss, rounded up, so the countdown shows 0:00 only once the code expired (also for a time
        // less than zero)
        public static string DescribeCountdown(TimeSpan remaining) {
            int seconds = remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
            return "Enter this code in the extension's options within " + (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                (seconds % 60).ToString("00", CultureInfo.InvariantCulture) + ".";
        }

        // How the code ended, in words for the dialog; family is the browser that paired, for Paired
        public static string DescribeEnd(ApiPairingEnd end, string family) {
            return ApiPairingFollow.DescribeEnd(end, family, Again, OtherCreators);
        }

        // The text shown when a pending code is ended because the API no longer runs (it was turned off, or a restart
        // failed), or null while it still runs or starts again: a code works only while the API listens
        public static string DescribeStop(LocalApiState state) {
            return state == LocalApiState.Off || state == LocalApiState.Failed ? "The local API stopped, so the code was cancelled." : null;
        }

        // The text shown when a code could not be made or followed (the message of the failure)
        public static string DescribeFailure(Exception ex) {
            return "The code was cancelled: " + ex.Message;
        }

        // A browser's row: when it paired (in that time zone), "Not paired", or "Unknown" when api-clients.txt can't be
        // used (clients is null then)
        public static string DescribeClient(IReadOnlyList<ApiClient> clients, string family, TimeZoneInfo zone) {
            if (clients == null) return "Unknown";
            ApiClient client = FindClient(clients, family);
            return client != null ? "Paired on " + TimeZoneInfo.ConvertTime(client.PairedAt, zone).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) : "Not paired";
        }

        // The family's line, or null when it is not paired or the file can't be used
        public static ApiClient FindClient(IReadOnlyList<ApiClient> clients, string family) {
            return clients?.FirstOrDefault(client => client.Family == family);
        }

        // Why an unpair removed nothing, from the file as it is now: it can't be used, the browser is no longer paired,
        // or it paired again since the dialog showed it (its line has another token)
        public static string DescribeUnpairFailure(IReadOnlyList<ApiClient> clients, string family) {
            string name = ApiPairingFollow.DisplayName(family);
            if (clients == null) return ApiClientStore.FileName + " could not be read, or it is damaged, a link, or others can read it, so nothing was unpaired.";
            if (FindClient(clients, family) == null) return "The " + name + " extension is not paired.";
            return "The " + name + " extension was paired again meanwhile, so it was not unpaired. Check the date and try again.";
        }
    }
}
