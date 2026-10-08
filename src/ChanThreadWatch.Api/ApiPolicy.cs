using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace JDP.Api {
    // The limits of the local API (maintainer decision D11) and the hooks that the tests replace. A host uses the
    // defaults; only the tests set other values.
    internal sealed class ApiPolicy {
        public const int DefaultMaxBodyBytes = 4 * 1024;
        public const int DefaultMaxUrlLength = 2048;
        public const int DefaultRequestsPerMinute = 120;
        public const int DefaultAddsPerMinute = 30;
        public const int DefaultThreadCap = 1000;
        public const long DefaultFreeSpaceFloorBytes = 1024L * 1024 * 1024;
        public const int MaxAuthorizationLength = 256;

        // Browser pairing (MP-7b). The iteration count is a protocol constant, never a server value, so a program that
        // is not this one cannot make the extension run a larger derivation.
        public const int PairingIterations = 600000;
        public const int MaxHellosPerCode = 3;
        public const int DefaultPairingRequestsPerMinute = 10;
        public const int DefaultProofRequestsPerMinute = 60;
        public const string DefaultServerName = "Chan Thread Watch";
        public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);

        private string _serverName = DefaultServerName;

        public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;
        public int MaxUrlLength { get; set; } = DefaultMaxUrlLength;
        public int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;
        public int AddsPerMinute { get; set; } = DefaultAddsPerMinute;
        public int ThreadCap { get; set; } = DefaultThreadCap;
        public long FreeSpaceFloorBytes { get; set; } = DefaultFreeSpaceFloorBytes;

        // The two routes without a token, POST /api/v1/pairing and POST /api/v1/proof, each have their own window; they
        // never use the window of the authenticated requests, nor it theirs
        public int PairingRequestsPerMinute { get; set; } = DefaultPairingRequestsPerMinute;
        public int ProofRequestsPerMinute { get; set; } = DefaultProofRequestsPerMinute;

        // The name a pairing extension shows before it saves the token ("Pair with <name> at 127.0.0.1:<port>?"). The
        // host sets it; a name that is empty, longer than 80 characters, or has a line break or another character that
        // is not printable is refused, since it is a line of the pairing proof.
        public string ServerName {
            get { return _serverName; }
            set {
                if (!ApiPairing.IsValidServerName(value)) throw new ArgumentException("The server name must be 1 to 80 printable characters without line breaks.", nameof(value));
                _serverName = value;
            }
        }

        // A name the setter takes, made from any text (a host's "ctw watch <version> on <machine>" with a long or odd
        // machine name): characters that are not printable are dropped, white space at the ends is trimmed, and the
        // name is cut to 80 characters; nothing left gives the default name
        public static string NormalizeServerName(string name) {
            string printable = new string((name ?? "").Where(c => ApiPairing.IsValidServerName(c.ToString())).ToArray()).Trim();
            string cut = printable.Length > ApiPairing.MaxServerNameLength ? printable.Substring(0, ApiPairing.MaxServerNameLength).TrimEnd() : printable;
            return cut.Length != 0 ? cut : DefaultServerName;
        }

        // A host's name: "<product> on <machine name>", normalized (the product names the host and its version, as
        // "ctw watch 1.40.0"). The default name when the machine name can't be read.
        public static string HostServerName(string product) {
            return HostServerName(product, () => Environment.MachineName);
        }

        // The same with the machine name from the delegate (the tests give one that throws)
        internal static string HostServerName(string product, Func<string> machineName) {
            string machine;
            try {
                machine = machineName();
            }
            catch (InvalidOperationException) {
                return DefaultServerName;
            }
            return NormalizeServerName(product + " on " + machine);
        }

        // The time for the pairing code's expiry and the paired date. The tests put a fake clock in its place.
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

        // The window of both rate limits (requests and adds per window). The tests make it long, so a test can never
        // span two windows.
        public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

        // How long a request waits for the owner thread (the UI thread or ctw's owner thread) before it gets 503
        public TimeSpan OwnerThreadTimeout { get; set; } = TimeSpan.FromSeconds(5);

        // How long the add-time DNS lookup may take
        public TimeSpan ResolveTimeout { get; set; } = TimeSpan.FromSeconds(5);

        // How long the add may take to download the thread's page for its name (the 4chan slug); then the thread is
        // added without it
        public TimeSpan ThreadNameLookupTimeout { get; set; } = TimeSpan.FromSeconds(5);

        // That download, always on the guarded clients (every connection and redirect hop is checked by the SSRF
        // guard). The tests put a fake one in its place.
        public Func<string, CancellationToken, Task<string>> FetchThreadPage { get; set; } = (url, token) => General.DownloadPageToStringAsync(url, true, token);

        // True once the host is exiting; checked on the owner thread before a thread is added
        public Func<bool> IsExiting { get; set; } = () => false;

        // The add-time DNS lookup. The tests put a fake one in its place.
        public Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; set; } = (host, token) => SSRFGuard.ResolveHost(host, token);

        // Free bytes on the download folder's volume, or null when unknown (the add goes ahead and the watcher
        // reports any problem). The tests put a fake one in its place.
        public Func<long?> GetFreeSpace { get; set; } = GetDownloadFolderFreeSpace;

        private static long? GetDownloadFolderFreeSpace() {
            try {
                return new DriveInfo(Settings.AbsoluteDownloadDirectory).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is FormatException || ex is UnauthorizedAccessException) {
                return null;
            }
        }
    }
}
