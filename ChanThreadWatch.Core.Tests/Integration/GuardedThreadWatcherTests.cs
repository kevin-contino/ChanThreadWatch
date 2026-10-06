using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // MP-7a L2b: a guarded watcher (a thread added through the local API) downloads its page, images and thumbnails,
    // and does the 4chan slug lookup of its constructor, only through the SSRF guard. A refusal is reported, not
    // retried, and the watcher keeps watching. Service mode stays off; the only server is on loopback.
    [TestClass]
    public class GuardedThreadWatcherTests : ThreadWatcherIntegrationTestBase {
        private const string ImageHost = "img.example.test";

        [TestInitialize]
        public void ResetGuard() {
            SSRFGuard.AllowSettingsChangeForTesting();
            Assert.IsFalse(SSRFGuard.ServiceMode, "these tests are about desktop mode");
            // Even if the guard were broken, nothing here connects anywhere but loopback
            SSRFGuard.BeforeConnect = addresses => {
                foreach (IPAddress address in addresses) {
                    if (!IPAddress.IsLoopback(address)) throw new InvalidOperationException("The test would have connected to " + address);
                }
            };
        }

        [TestCleanup]
        public void RestoreGuard() {
            SSRFGuard.AllowSettingsChangeForTesting();
            SSRFGuard.AllowLoopbackForTesting = false;
            SSRFGuard.AllowedHosts = null;
            SSRFGuard.BeforeConnect = addresses => { };
            SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
        }

        [TestMethod]
        public void TheOldConstructorIsUnguarded() {
            Assert.IsFalse(new ThreadWatcher("http://127.0.0.1:1/wg/thread/100").Guarded);
            Assert.IsTrue(new ThreadWatcher("http://127.0.0.1:1/wg/thread/100", true).Guarded);
            Assert.IsFalse(new ThreadWatcher("http://127.0.0.1:1/wg/thread/100", false).Guarded);
        }

        // With the loopback test hook, a guarded watcher downloads the whole thread through the guarded clients
        [TestMethod]
        public void AGuardedWatcherDownloadsAnAllowedThread() {
            SSRFGuard.AllowLoopbackForTesting = true;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = new ThreadWatcher(server.URL(FourChanThreadFixture.ThreadPath), true) { OneTimeDownload = true };

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(watcher));

            foreach (KeyValuePair<string, byte[]> image in fixture.Images) {
                CollectionAssert.AreEqual(image.Value, File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, FourChanThreadFixture.FileName(image.Key))), image.Key);
            }
            Assert.AreEqual(0, watcher.FailedFileCount);
        }

        // The thread page on loopback is refused in each check, once (no retry), and the watcher keeps watching
        [TestMethod]
        public void AGuardedWatcherIsRefusedALocalPageAndKeepsWatching() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string pageURL = server.URL(FourChanThreadFixture.ThreadPath);
            int pageRequestStarts = 0;
            ThreadWatcher.BeforeRequestStart = url => { if (url == pageURL) Interlocked.Increment(ref pageRequestStarts); };
            ThreadWatcher watcher = new ThreadWatcher(pageURL, true);
            var checkErrors = new List<string>();

            RunChecks(watcher, 2, check => checkErrors.Add(watcher.CheckError));
            checkErrors.Add(watcher.CheckError);

            Assert.AreEqual(0, server.ConnectionCount);
            Assert.AreEqual(2, pageRequestStarts);
            foreach (string checkError in checkErrors) {
                Assert.IsNotNull(checkError);
                StringAssert.Contains(checkError, "blocked");
            }
            // Stopped by RunChecks only
            Assert.AreEqual(StopReason.UserRequest, watcher.StopReason);
        }

        // The page is allowed; the images and thumbnails are on a name that resolves to a private address. Each file
        // is refused once (no retry), reported, and nothing is saved.
        [TestMethod]
        public void AGuardedWatcherIsRefusedFilesOnAPrivateAddress() {
            SSRFGuard.AllowedHosts = new[] { PageHost };
            int imageHostLookups = 0;
            SSRFGuard.ResolveHost = (host, cancellationToken) => {
                if (host == ImageHost) Interlocked.Increment(ref imageHostLookups);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
            };
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            string fileBaseURL = "http://" + ImageHost + ":" + server.Port;
            fixture.RouteThread(server, fileBaseURL, fileBaseURL);
            ThreadWatcher watcher = new ThreadWatcher(server.URL(FourChanThreadFixture.ThreadPath), true) { OneTimeDownload = true };
            int logLength = ReadLog().Length;

            RunToStop(watcher);

            int fileCount = FourChanThreadFixture.ImagePaths.Length + FourChanThreadFixture.ThumbPaths.Length;
            Assert.AreEqual(fileCount, imageHostLookups);
            Assert.AreEqual(fileCount, watcher.FailedFileCount);
            Assert.HasCount(1, server.Requests);
            Assert.IsEmpty(Directory.GetFiles(watcher.ThreadDownloadDirectory, "17*"));
            StringAssert.Contains(ReadLog().Substring(logLength), "requests to " + ImageHost + " are blocked");
        }

        // Fix 9: a refused file is skipped, like one that is too large: it is logged once and not tried again in
        // later checks
        [TestMethod]
        public void ARefusedFileIsNotTriedAgainInLaterChecks() {
            SSRFGuard.AllowedHosts = new[] { PageHost };
            int imageHostLookups = 0;
            SSRFGuard.ResolveHost = (host, cancellationToken) => {
                if (host == ImageHost) Interlocked.Increment(ref imageHostLookups);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
            };
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            string fileBaseURL = "http://" + ImageHost + ":" + server.Port;
            fixture.RouteThread(server, fileBaseURL, fileBaseURL);
            ThreadWatcher watcher = new ThreadWatcher(server.URL(FourChanThreadFixture.ThreadPath), true);
            string logBefore = ReadLog();

            RunChecks(watcher, 3);

            int fileCount = FourChanThreadFixture.ImagePaths.Length + FourChanThreadFixture.ThumbPaths.Length;
            Assert.AreEqual(fileCount, imageHostLookups);
            string log = ReadLog().Substring(logBefore.Length);
            string firstImage = fileBaseURL + FourChanThreadFixture.ImagePaths[0];
            Assert.AreEqual(1, log.Split("Error downloading file " + firstImage + ":").Length - 1);
        }

        // Rounds 3 and 4: a guarded request's proxy refusal says what to do, on the file's (or page's) one error line;
        // a proxy refusal of service mode (an unguarded request) and any other refusal do not mention the API
        [TestMethod]
        public void AProxyRefusalOfAGuardedRequestSaysToRemoveTheProxy() {
            const string url = "http://img.example.test/a.jpg";
            var target = new Uri(url);
            var proxy = new DnsEndPoint("proxy.example.test", 8080);

            BlockedAddressException guarded = Assert.ThrowsExactly<BlockedAddressException>(() => SSRFGuard.ThrowIfProxied(proxy, target, true));
            BlockedAddressException serviceMode = Assert.ThrowsExactly<BlockedAddressException>(() => SSRFGuard.ThrowIfProxied(proxy, target));

            Assert.AreEqual("requests to img.example.test are blocked: it would be reached through a proxy (a thread added through the local API refuses a proxy: remove the proxy, then add the thread again)", ThreadWatcher.DescribeDownloadError(guarded, url));
            Assert.AreEqual("requests to img.example.test are blocked: it would be reached through a proxy", ThreadWatcher.DescribeDownloadError(serviceMode, url));
            Assert.AreEqual("requests to img.example.test are blocked: it is a local or private address", ThreadWatcher.DescribeDownloadError(new BlockedAddressException("img.example.test"), url));
        }

        // The slug lookup in the constructor (UseSlug, a 4chan URL without a slug) is guarded too
        [TestMethod]
        public void TheSlugLookupOfAGuardedWatcherIsGuarded() {
            Settings.UseSlug = true;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string pageURL = server.URL(FourChanThreadFixture.ThreadPath);

            ThreadWatcher guarded = new ThreadWatcher(pageURL, true);

            Assert.AreEqual(0, server.ConnectionCount);
            Assert.AreEqual(FourChanThreadFixture.ThreadName, guarded.ThreadName);

            new ThreadWatcher(pageURL, false);

            Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
        }

        // A thread followed from a guarded thread is guarded (CreateChildThreadInfo), and one from an unguarded thread is not
        [TestMethod]
        public void AChildThreadInheritsTheMark() {
            ThreadWatcher guarded = new ThreadWatcher("http://127.0.0.1:1/wg/thread/100", true);
            ThreadWatcher unguarded = new ThreadWatcher("http://127.0.0.1:1/wg/thread/101");

            Assert.IsTrue(guarded.CreateChildThreadInfo("http://127.0.0.1:1/wg/thread/102", DateTime.Now, false).Guarded);
            Assert.IsFalse(unguarded.CreateChildThreadInfo("http://127.0.0.1:1/wg/thread/102", DateTime.Now, false).Guarded);
        }
    }
}
