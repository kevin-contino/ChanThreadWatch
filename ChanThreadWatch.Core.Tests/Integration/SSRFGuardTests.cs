using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // Security item 9: in service mode no request reaches a loopback, private, link-local or other
    // local address, on any hop; in desktop mode (the app today) nothing is blocked. Every blocked
    // case is refused before a connection is made, and the only server is on loopback.
    [TestClass]
    public class SSRFGuardTests {
        private const string Sentinel = "ssrf-sentinel";
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
        public void RestoreDesktopMode() {
            SSRFGuard.AllowSettingsChangeForTesting();
            SSRFGuard.ServiceMode = false;
            SSRFGuard.AllowLoopbackForTesting = false;
            SSRFGuard.AllowedHosts = null;
            SSRFGuard.BeforeConnect = addresses => { };
            SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
            _server.Dispose();
            ConnectionManager.ResetForTesting();
        }

        // The defaults as a fresh load of Core has them, whatever earlier tests set in this process:
        // desktop mode, no allowlist, and the loopback test hook off
        [TestMethod]
        public void ServiceModeAndTheLoopbackHookAreOffByDefault() {
            var context = new AssemblyLoadContext("ssrf-defaults", true);
            try {
                Assembly core = context.LoadFromAssemblyPath(typeof(SSRFGuard).Assembly.Location);
                Type guard = core.GetType("JDP.SSRFGuard", true);
                const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                Assert.IsFalse((bool)guard.GetProperty("ServiceMode", statics).GetValue(null));
                Assert.IsFalse((bool)guard.GetProperty("AllowLoopbackForTesting", statics).GetValue(null));
                Assert.IsEmpty((string[])guard.GetProperty("AllowedHosts", statics).GetValue(null));
            }
            finally {
                context.Unload();
            }
        }

        // With the hook at its default, service mode blocks the loopback test server itself
        [TestMethod]
        public void ServiceModeBlocksLoopbackWithoutTheTestHook() {
            SSRFGuard.ServiceMode = true;

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x")));

            Assert.AreEqual(0, _server.ConnectionCount);
        }

        // A literal private or local address, a name that resolves to 127.0.0.1, IPv6 loopback and
        // the decimal form of 127.0.0.1
        [TestMethod]
        [DataRow("10.0.0.1")]
        [DataRow("192.168.1.1")]
        [DataRow("169.254.169.254")]
        [DataRow("0.0.0.0")]
        [DataRow("localhost")]
        [DataRow("[::1]")]
        [DataRow("[::ffff:127.0.0.1]")]
        [DataRow("2130706433")]
        [DataRow("0x7f.0.0.1")]
        [DataRow("0177.0.0.1")]
        public void ServiceModeBlocksLocalAddresses(string host) {
            SSRFGuard.ServiceMode = true;

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x", host)));

            Assert.AreEqual(0, _server.ConnectionCount);
        }

        // The forms of 127.0.0.1 reach the loopback server in desktop mode
        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("localhost")]
        [DataRow("2130706433")]
        public void DesktopModeBlocksNothing(string host) {
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x", host)));
        }

        // An HTTP redirect from an allowed host to a private address is refused at that hop
        [TestMethod]
        public void ServiceModeBlocksARedirectToAPrivateAddress() {
            EnterServiceModeWithLoopbackAllowed();
            _server.Route("/start", Redirect("http://10.0.0.1:" + _server.Port + "/x"));

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/start")));
            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/start")));

            Assert.HasCount(2, _server.RequestsTo("/start"));
            Assert.HasCount(2, _server.Requests);
        }

        // A meta refresh to the cloud metadata address is refused
        [TestMethod]
        public void ServiceModeBlocksAMetaRefreshToTheMetadataAddress() {
            EnterServiceModeWithLoopbackAllowed();
            _server.Route("/start", LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=http://169.254.169.254/latest/meta-data/\"></head></html>"));

            Assert.IsInstanceOfType<BlockedAddressException>(DownloadAsync(_server.URL("/start")));

            Assert.HasCount(1, _server.Requests);
        }

        // file: and ftp: are never followed, in service mode too: a redirect ends with its own status
        [TestMethod]
        [DataRow("file:///C:/Windows/win.ini")]
        [DataRow("ftp://127.0.0.1/x")]
        public void ServiceModeNeverFollowsANonHttpScheme(string target) {
            EnterServiceModeWithLoopbackAllowed();
            _server.Route("/redirect", Redirect(target));
            _server.Route("/meta", LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + target + "\"></head></html>"));

            Assert.AreEqual("HTTP 302 Found", DownloadAsync(_server.URL("/redirect")).Message);
            Assert.IsInstanceOfType<NotSupportedException>(DownloadAsync(_server.URL("/meta")));

            Assert.HasCount(2, _server.Requests);
            Assert.AreEqual(2, _server.ConnectionCount);
        }

        // Service mode and the allowlist cannot change once a connection has been made, since a pooled
        // connection would not be checked again; setting the same value is allowed
        [TestMethod]
        public void TheSettingsCannotChangeAfterAConnection() {
            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x")));

            Assert.ThrowsExactly<InvalidOperationException>(() => SSRFGuard.ServiceMode = true);
            Assert.ThrowsExactly<InvalidOperationException>(() => SSRFGuard.AllowedHosts = new[] { "lan.example.test" });
            SSRFGuard.ServiceMode = false;
            SSRFGuard.AllowedHosts = new string[0];
            Assert.IsFalse(SSRFGuard.ServiceMode);
            Assert.IsEmpty(SSRFGuard.AllowedHosts);
        }

        // One private address in a DNS answer blocks the host, so an answer cannot mix one in
        [TestMethod]
        public void ADNSAnswerWithAPrivateAddressIsBlocked() {
            SSRFGuard.ServiceMode = true;
            SSRFGuard.ResolveHost = (host, cancellationToken) => System.Threading.Tasks.Task.FromResult(new[] { IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.1") });

            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString("http://mixed.example.test:" + _server.Port + "/x"));
        }

        // A connection to a proxy instead of the request's host is refused in service mode
        [TestMethod]
        public void ServiceModeRefusesAProxiedConnection() {
            var target = new Uri("http://boards.example.test/a/thread/1");

            SSRFGuard.ThrowIfProxied(new DnsEndPoint("boards.example.test", 80), target);
            SSRFGuard.ThrowIfProxied(new DnsEndPoint("[::1]", 8080), new Uri("http://[::1]:8080/x"));
            Assert.ThrowsExactly<BlockedAddressException>(() => SSRFGuard.ThrowIfProxied(new DnsEndPoint("proxy.example.test", 8080), target));
            Assert.ThrowsExactly<BlockedAddressException>(() => SSRFGuard.ThrowIfProxied(new DnsEndPoint("boards.example.test", 8080), target));
        }

        // A host on the allowlist (a LAN board) gets through; the same server under another name does not
        [TestMethod]
        public void TheAllowlistLetsAHostThrough() {
            SSRFGuard.ServiceMode = true;
            SSRFGuard.AllowedHosts = new[] { "localhost" };

            Assert.AreEqual(Sentinel, General.DownloadPageToString(_server.URL("/x", "localhost")));
            Assert.ThrowsExactly<BlockedAddressException>(() => General.DownloadPageToString(_server.URL("/x")));
        }

        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("127.255.255.254")]
        [DataRow("10.1.2.3")]
        [DataRow("172.16.0.1")]
        [DataRow("172.31.255.255")]
        [DataRow("192.168.0.1")]
        [DataRow("169.254.169.254")]
        [DataRow("100.64.0.1")]
        [DataRow("100.127.255.255")]
        [DataRow("0.0.0.0")]
        [DataRow("224.0.0.1")]
        [DataRow("255.255.255.255")]
        [DataRow("::")]
        [DataRow("::1")]
        [DataRow("fc00::1")]
        [DataRow("fd12:3456::1")]
        [DataRow("fe80::1")]
        [DataRow("ff02::1")]
        [DataRow("::ffff:127.0.0.1")]
        [DataRow("::ffff:10.0.0.1")]
        [DataRow("::ffff:169.254.169.254")]
        [DataRow("::127.0.0.1")]
        [DataRow("64:ff9b::a00:1")]
        [DataRow("::0.0.0.2")]
        [DataRow("2002:a00:1::1")]
        [DataRow("2001:0:a00:1::1")]
        [DataRow("198.18.0.1")]
        [DataRow("192.0.0.8")]
        public void LocalAndPrivateAddressesAreBlocked(string address) {
            Assert.IsTrue(SSRFGuard.IsBlockedAddress(IPAddress.Parse(address)));
        }

        [TestMethod]
        [DataRow("8.8.8.8")]
        [DataRow("172.32.0.1")]
        [DataRow("172.15.255.255")]
        [DataRow("100.128.0.1")]
        [DataRow("192.169.0.1")]
        [DataRow("2606:4700::1111")]
        [DataRow("::ffff:8.8.8.8")]
        public void PublicAddressesAreNotBlocked(string address) {
            Assert.IsFalse(SSRFGuard.IsBlockedAddress(IPAddress.Parse(address)));
        }

        private static void EnterServiceModeWithLoopbackAllowed() {
            SSRFGuard.ServiceMode = true;
            SSRFGuard.AllowLoopbackForTesting = true;
        }

        private static LoopbackResponse Redirect(string location) {
            return LoopbackResponse.StatusOnly(302, "Found").WithHeader("Location", location);
        }

        // Runs General.DownloadAsync to its end and returns its error (null if it completed); no
        // data may reach the client
        private static Exception DownloadAsync(string url) {
            var done = new ManualResetEvent(false);
            var data = new MemoryStream();
            Exception error = null;
            General.DownloadAsync(url, null, null, false, null, r => { }, (b, n) => { lock (data) data.Write(b, 0, n); }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download did not end");
            lock (data) Assert.DoesNotContain(Sentinel, System.Text.Encoding.ASCII.GetString(data.ToArray()));
            return error;
        }
    }
}
