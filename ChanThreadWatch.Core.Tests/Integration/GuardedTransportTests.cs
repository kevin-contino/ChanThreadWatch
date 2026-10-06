using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // MP-7a L2b: a guarded download (a thread added through the local API) never connects to a loopback, private,
    // link-local or other local address, nor through a proxy, on any hop, while service mode stays off and an
    // unguarded download still reaches them. Every blocked case is refused before a connection is made, and the only
    // servers are on loopback.
    [TestClass]
    public class GuardedTransportTests {
        private const string Sentinel = "guard-sentinel";
        private LoopbackHttpServer _server;

        [ClassInitialize]
        public static void LoadEmptySettings(TestContext context) {
            // Keep the settings store in the test output folder (no settings file there) instead of AppData
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
        }

        [TestInitialize]
        public void StartServer() {
            // Earlier tests have connected; these tests use their own servers, so no pooled connection carries over
            SSRFGuard.AllowSettingsChangeForTesting();
            Assert.IsFalse(SSRFGuard.ServiceMode, "these tests are about desktop mode");
            _server = new LoopbackHttpServer();
            _server.Route("/x", LoopbackResponse.Text(Sentinel));
            // Even if the guard were broken, nothing here connects anywhere but loopback
            SSRFGuard.BeforeConnect = addresses => {
                foreach (IPAddress address in addresses) {
                    if (!IPAddress.IsLoopback(address)) throw new InvalidOperationException("The test would have connected to " + address);
                }
            };
        }

        [TestCleanup]
        public void RestoreDefaults() {
            SSRFGuard.AllowSettingsChangeForTesting();
            SSRFGuard.ServiceMode = false;
            SSRFGuard.AllowLoopbackForTesting = false;
            SSRFGuard.AllowedHosts = null;
            SSRFGuard.BeforeConnect = addresses => { };
            SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
            General.UseUnguardedClientsForTesting = false;
            _server.Dispose();
            ConnectionManager.ResetForTesting();
        }

        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("10.0.0.1")]
        [DataRow("192.168.1.1")]
        [DataRow("169.254.169.254")]
        [DataRow("0.0.0.0")]
        [DataRow("localhost")]
        [DataRow("[::1]")]
        [DataRow("[::ffff:127.0.0.1]")]
        [DataRow("2130706433")]
        public void AGuardedDownloadBlocksLocalAddresses(string host) {
            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x", host), true));
            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/x", host), true));

            Assert.AreEqual(0, _server.ConnectionCount);
            Assert.IsFalse(SSRFGuard.ServiceMode);
        }

        // The same server, unguarded: desktop mode is unchanged
        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("localhost")]
        public void AnUnguardedDownloadStillReachesLoopback(string host) {
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x", host)));
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x", host), false));
            Assert.IsNull(DownloadAsync(_server.URL("/x", host), false));
        }

        // With the loopback test hook, the guarded path itself works end to end
        [TestMethod]
        public void AGuardedDownloadReachesAnAllowedAddress() {
            SSRFGuard.AllowLoopbackForTesting = true;

            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x"), true));
            Assert.IsNull(DownloadAsync(_server.URL("/x"), true));
        }

        // An HTTP redirect from an allowed host to a private address is refused at that hop
        [TestMethod]
        public void AGuardedDownloadBlocksARedirectToAPrivateAddress() {
            SSRFGuard.AllowLoopbackForTesting = true;
            _server.Route("/start", Redirect("http://10.0.0.1:" + _server.Port + "/x"));

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/start"), true));
            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/start"), true));

            Assert.HasCount(2, _server.RequestsTo("/start"));
            Assert.HasCount(2, _server.Requests);
        }

        // A redirect to a name that resolves to a private address is refused too: every hop goes out on the guarded client
        [TestMethod]
        public void AGuardedDownloadBlocksARedirectToANameThatResolvesToAPrivateAddress() {
            _server.Route("/start", Redirect("http://redirect.example.test:" + _server.Port + "/x"));
            SSRFGuard.ResolveHost = (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("192.168.0.5") });
            // The first hop is allowed by its address
            SSRFGuard.AllowedHosts = new[] { "127.0.0.1" };

            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/start"), true));

            Assert.HasCount(1, _server.Requests);
        }

        // A meta refresh to the cloud metadata address is refused
        [TestMethod]
        public void AGuardedDownloadBlocksAMetaRefreshToTheMetadataAddress() {
            SSRFGuard.AllowLoopbackForTesting = true;
            _server.Route("/start", LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=http://169.254.169.254/latest/meta-data/\"></head></html>"));

            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/start"), true));

            Assert.HasCount(1, _server.Requests);
        }

        // One private address in a DNS answer blocks the host, so an answer cannot mix one in
        [TestMethod]
        public void AGuardedDownloadBlocksADNSAnswerWithAPrivateAddress() {
            SSRFGuard.ResolveHost = (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.1") });

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString("http://mixed.example.test:" + _server.Port + "/x", true));
        }

        // D6: the same allowlist as service mode
        [TestMethod]
        public void TheAllowlistLetsAGuardedHostThrough() {
            SSRFGuard.AllowedHosts = new[] { "localhost" };

            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x", "localhost"), true));
            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x"), true));
        }

        // D2: a guarded connection through a proxy is refused (the proxy resolves the target, so no check could stop a
        // private one); the same handler unguarded goes to the proxy. The proxy here is the loopback server.
        [TestMethod]
        public void AGuardedRequestRefusesAProxy() {
            SSRFGuard.AllowLoopbackForTesting = true;
            var target = new Uri("http://boards.example.test/x");

            Exception guarded = SendThroughProxy(target, true);
            Assert.IsInstanceOfType<BlockedAddressException>(Unwrap(guarded));
            StringAssert.Contains(Unwrap(guarded).Message, "through a proxy");
            Assert.AreEqual(0, _server.ConnectionCount);

            Assert.IsNull(SendThroughProxy(target, false));
            Assert.AreEqual(1, _server.ConnectionCount);
            Assert.IsTrue(((BlockedAddressException)Unwrap(guarded)).IsGuardedRequest);
        }

        // Round 4: service mode refuses the proxy too, as a refusal that is not a guarded request's (no API advice)
        [TestMethod]
        public void AServiceModeProxyRefusalIsNotAGuardedRequests() {
            SSRFGuard.ServiceMode = true;
            SSRFGuard.AllowLoopbackForTesting = true;

            BlockedAddressException refused = (BlockedAddressException)Unwrap(SendThroughProxy(new Uri("http://boards.example.test/x"), false));

            Assert.IsTrue(refused.IsProxied);
            Assert.IsFalse(refused.IsGuardedRequest);
            Assert.DoesNotContain("local API", ThreadWatcher.DescribeDownloadError(refused, "http://boards.example.test/x"));
            Assert.AreEqual(0, _server.ConnectionCount);
        }

        // Tripwire: a guarded request sent on an unguarded client (a mix-up) is refused instead of connecting
        [TestMethod]
        public void AGuardedRequestOnAnUnguardedClientIsRefused() {
            using (SocketsHttpHandler handler = General.CreateHttpHandler(TimeSpan.Zero, false))
            using (HttpClient client = new HttpClient(handler)) {
                HttpRequestMessage request = General.BuildWebRequest(new Uri(_server.URL("/x")), null, null, null, true);

                HttpRequestException ex = Assert.ThrowsExactly<HttpRequestException>(() => client.Send(request));

                Assert.IsInstanceOfType<BlockedAddressException>(Unwrap(ex));
                Assert.AreEqual(0, _server.ConnectionCount);
            }
        }

        // The connect-time tripwire can't see a guarded request on a pooled connection, so SendAsync checks the client
        // before sending; the tripwire takes a request without options or without a URI
        [TestMethod]
        public void AGuardedRequestIsCheckedAgainstTheClientBeforeItIsSent() {
            Uri uri = new Uri(_server.URL("/x"));
            using (HttpRequestMessage guarded = General.BuildWebRequest(uri, null, null, null, true))
            using (HttpRequestMessage unguarded = General.BuildWebRequest(uri, null, null, null, false)) {
                Assert.ThrowsExactly<BlockedAddressException>(() => General.ThrowIfGuardedRequestOnUnguardedClient(guarded, General.GetHttpClient(false, false)));
                Assert.ThrowsExactly<BlockedAddressException>(() => General.ThrowIfGuardedRequestOnUnguardedClient(guarded, General.GetHttpClient(true, false)));
                General.ThrowIfGuardedRequestOnUnguardedClient(guarded, General.GetHttpClient(false, true));
                General.ThrowIfGuardedRequestOnUnguardedClient(guarded, General.GetHttpClient(true, true));
                General.ThrowIfGuardedRequestOnUnguardedClient(unguarded, General.GetHttpClient(false, false));
            }
            SSRFGuard.ThrowIfGuardedRequest(null);
            SSRFGuard.ThrowIfGuardedRequest(new HttpRequestMessage());
            Assert.AreEqual(0, _server.ConnectionCount);
        }

        // A guarded request that SendAsync is about to send on an unguarded client (a mix-up, made here by the test
        // seam) is refused before it goes out, also when a pooled connection would carry it (the connect-time
        // tripwire never runs then)
        [TestMethod]
        public void SendAsyncRefusesAGuardedRequestOnAnUnguardedClient() {
            _server.KeepAlive = true;
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x")));
            General.UseUnguardedClientsForTesting = true;

            BlockedAddressException ex = Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x"), true));

            StringAssert.Contains(ex.Message, "about to go out on a client that is not checked");
            Assert.HasCount(1, _server.Requests);
        }

        // The guarded clients have their own connection pools: a connection an unguarded download left open is never
        // used by a guarded one, which is checked when it connects
        [TestMethod]
        public void AGuardedDownloadNeverReusesAnUnguardedConnection() {
            _server.KeepAlive = true;
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x")));
            Assert.AreEqual(1, _server.ConnectionCount);

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x"), true));

            Assert.AreEqual(1, _server.ConnectionCount);
            Assert.HasCount(1, _server.Requests);
        }

        private static LoopbackResponse Redirect(string location) {
            return LoopbackResponse.StatusOnly(302, "Found").WithHeader("Location", location);
        }

        private Exception SendThroughProxy(Uri target, bool guarded) {
            using (SocketsHttpHandler handler = General.CreateHttpHandler(TimeSpan.Zero, guarded))
            using (HttpClient client = new HttpClient(handler)) {
                handler.Proxy = new WebProxy(_server.BaseURL());
                try {
                    using (HttpResponseMessage response = client.Send(General.BuildWebRequest(target, null, null, null, guarded))) {
                        return null;
                    }
                }
                catch (HttpRequestException ex) {
                    return ex;
                }
            }
        }

        private static Exception Unwrap(Exception ex) {
            for (Exception inner = ex; inner != null; inner = inner.InnerException) {
                if (inner is BlockedAddressException) return inner;
            }
            return ex;
        }

        // Runs General.DownloadAsync to its end and returns its error (null if it completed); a blocked download
        // delivers no data
        private static Exception DownloadAsync(string url, bool guarded) {
            var done = new ManualResetEvent(false);
            var data = new MemoryStream();
            Exception error = null;
            General.DownloadAsync(url, null, null, false, guarded, null, r => { }, (b, n) => { lock (data) data.Write(b, 0, n); }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download did not end");
            if (error != null) {
                lock (data) Assert.DoesNotContain(Sentinel, System.Text.Encoding.ASCII.GetString(data.ToArray()));
            }
            return error;
        }
    }
}
