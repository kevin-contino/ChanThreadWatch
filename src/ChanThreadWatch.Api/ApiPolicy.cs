using System;
using System.IO;
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

        public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;
        public int MaxUrlLength { get; set; } = DefaultMaxUrlLength;
        public int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;
        public int AddsPerMinute { get; set; } = DefaultAddsPerMinute;
        public int ThreadCap { get; set; } = DefaultThreadCap;
        public long FreeSpaceFloorBytes { get; set; } = DefaultFreeSpaceFloorBytes;

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
