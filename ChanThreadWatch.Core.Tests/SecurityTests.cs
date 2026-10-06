using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Runtime.CompilerServices;
using System.Threading;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class SecurityTests {
        [ClassInitialize]
        public static void LoadEmptySettings(TestContext context) {
            // Keep the settings store in the test output folder (no settings file there) instead of AppData
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
        }

        // S1: the transport keeps the default certificate validation and lets the OS pick the TLS version
        [TestMethod]
        public void ThreadWatcherDoesNotDisableCertificateValidation() {
            RuntimeHelpers.RunClassConstructor(typeof(ThreadWatcher).TypeHandle);

            using (SocketsHttpHandler handler = General.CreateHttpHandler(TimeSpan.Zero, false)) {
                Assert.IsNull(handler.SslOptions.RemoteCertificateValidationCallback);
                Assert.AreEqual(SslProtocols.None, handler.SslOptions.EnabledSslProtocols);
                Assert.IsFalse(handler.UseCookies);
                Assert.IsFalse(handler.AllowAutoRedirect);
                Assert.AreEqual(DecompressionMethods.None, handler.AutomaticDecompression);
            }
        }

        // S2
        [TestMethod]
        [DataRow("https://boards.example.org/a/thread/1", "https://boards.example.org/a/src/1.jpg", true)]
        [DataRow("https://boards.example.org/a/thread/1", "https://BOARDS.example.org:443/x", true)]
        [DataRow("https://boards.example.org/a/thread/1", "https://i.example.org/a/1.jpg", false)]
        [DataRow("https://boards.example.org/a/thread/1", "http://boards.example.org/a/src/1.jpg", false)]
        [DataRow("https://boards.example.org/a/thread/1", "https://boards.example.org:8443/x", false)]
        [DataRow("https://boards.example.org/a/thread/1", "https://boards.example.org.evil.com/x", false)]
        [DataRow("https://boards.example.org/a/thread/1", "https://boards.example.org@evil.com/x", false)]
        [DataRow("https://boards.example.org/a/thread/1", "not a url", false)]
        public void IsSameOriginComparesSchemeHostAndPort(string a, string b, bool expected) {
            Assert.AreEqual(expected, General.IsSameOrigin(a, b));
        }

        [TestMethod]
        public void GetAuthForURLOnlyReturnsAuthForSameOrigin() {
            const string page = "https://boards.example.org/a/thread/1";

            Assert.AreEqual("user:pass", General.GetAuthForURL("user:pass", page, "https://boards.example.org/a/thread/2"));
            Assert.IsNull(General.GetAuthForURL("user:pass", page, "https://evil.example.com/a/thread/2"));
            Assert.IsNull(General.GetAuthForURL(null, page, page));
        }

        [TestMethod]
        public void MetaRefreshToOtherOriginDropsCredentials() {
            using (var target = new LoopbackHttpServer())
            using (var start = new LoopbackHttpServer()) {
                target.Route("/x", OkResponse);
                start.Route("/start", MetaRefreshResponse(target.URL("/x", "localhost")));

                Download(start.URL("/start"), "user:pass", null);

                Assert.AreEqual("user:pass", start.Requests[0].BasicAuth);
                Assert.IsNull(target.Requests[0].Header("Authorization"));
            }
        }

        // S2: an HTTP redirect drops the Authorization header (General.SendAsync drops it on every redirect)
        [TestMethod]
        public void HttpRedirectToOtherOriginDropsCredentials() {
            using (var target = new LoopbackHttpServer())
            using (var start = new LoopbackHttpServer()) {
                target.Route("/x", OkResponse);
                start.Route("/start", LoopbackResponse.Raw("HTTP/1.1 302 Found\r\nLocation: " + target.URL("/x", "localhost") + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));

                Download(start.URL("/start"), "user:pass", null);

                Assert.AreEqual("user:pass", start.Requests[0].BasicAuth);
                Assert.IsNull(target.Requests[0].Header("Authorization"));
            }
        }

        [TestMethod]
        public void MetaRefreshToSameOriginKeepsCredentials() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/start", MetaRefreshResponse(server.URL("/next")));
                server.Route("/next", OkResponse);

                Download(server.URL("/start"), "user:pass", null);

                Assert.HasCount(2, server.Requests);
                Assert.AreEqual("user:pass", server.Requests[1].BasicAuth);
            }
        }

        // S3
        [TestMethod]
        [DataRow(".", "")]
        [DataRow("..", "")]
        [DataRow("...", "")]
        [DataRow(". .", "")]
        [DataRow("name. ", "name")]
        [DataRow("a..", "a")]
        public void CleanFileNameNeverReturnsDotSegments(string input, string expected) {
            Assert.AreEqual(expected, General.CleanFileName(input));
        }

        // The backslash is removed on every OS, since a SaveDir read on Linux or macOS treats it as a separator
        [TestMethod]
        public void CleanFileNameNeverReturnsDotSegmentsAcrossBackslashes() {
            Assert.AreEqual("....x", General.CleanFileName("../..\\x"));
        }

        [TestMethod]
        [DataRow("CON", "_CON")]
        [DataRow("con", "_con")]
        [DataRow("nul.txt", "_nul.txt")]
        [DataRow("COM1.jpg", "_COM1.jpg")]
        [DataRow("LPT9", "_LPT9")]
        [DataRow("AUX .png", "_AUX .png")]
        [DataRow("NUL ", "_NUL")]
        [DataRow("CONSOLE", "CONSOLE")]
        [DataRow("CON.", "_CON")]
        [DataRow("CONIN$", "_CONIN$")]
        [DataRow("com\u00B9.jpg", "_com\u00B9.jpg")]
        [DataRow("LPT\u00B3", "_LPT\u00B3")]
        [DataRow("COM10", "COM10")]
        [DataRow("icon.png", "icon.png")]
        public void CleanFileNamePrefixesReservedDeviceNames(string input, string expected) {
            Assert.AreEqual(expected, General.CleanFileName(input));
        }

        // S8
        [TestMethod]
        public void DownloadSendsRefererWithoutAuth() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/image.jpg", OkResponse);

                Download(server.URL("/image.jpg"), null, "https://boards.example.org/a/thread/1");

                Assert.AreEqual("https://boards.example.org/a/thread/1", server.Requests[0].Header("Referer"));
            }
        }

        // A Referer or custom User-Agent with a line break is never sent as extra header lines
        [TestMethod]
        public void HeaderValuesCannotInjectHeaders() {
            Settings.UseCustomUserAgent = true;
            Settings.CustomUserAgent = "Agent/1.0\r\nX-Injected: user-agent";
            try {
                using (var server = new LoopbackHttpServer()) {
                    server.Route("/image.jpg", OkResponse);

                    Download(server.URL("/image.jpg"), null, "https://boards.example.org/a/thread/1\r\nX-Injected: referer");

                    RecordedRequest request = server.Requests[0];
                    Assert.IsNull(request.Header("X-Injected"), request.Raw);
                    // Settings keep each value on one line, and the transport would drop one that is not
                    Assert.AreEqual("Agent/1.0 X-Injected: user-agent", request.Header("User-Agent"), request.Raw);
                    Assert.IsNull(request.Header("Referer"), request.Raw);
                    Assert.HasCount(1, server.Requests);
                }
            }
            finally {
                Settings.UseCustomUserAgent = false;
                Settings.CustomUserAgent = null;
            }
        }

        // HTTP/3 would connect without the SSRF guard's ConnectCallback, so every request is HTTP/1.1 exactly
        [TestMethod]
        public void RequestsUseHTTP11Exactly() {
            HttpRequestMessage request = General.BuildWebRequest(new Uri("https://boards.example.org/a/thread/1"), "user:pass", "https://boards.example.org/", DateTime.Now, false);

            Assert.AreEqual(HttpVersion.Version11, request.Version);
            Assert.AreEqual(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        }

        private static readonly LoopbackResponse OkResponse = LoopbackResponse.Text("ok");

        private static LoopbackResponse MetaRefreshResponse(string url) {
            return LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head></html>");
        }

        private static void Download(string url, string auth, string referer) {
            var done = new ManualResetEvent(false);
            Exception error = null;
            General.DownloadAsync(url, auth, referer, false, false, null, r => { }, (b, n) => { }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download timed out");
            Assert.IsNull(error, error?.ToString());
        }

        // A thread URL with a login: the links the saved page makes absolute leave the login out
        private const string PageWithLogin = "http://user:pw-7Qz@boards.example.test/a/thread/1";

        [TestMethod]
        public void SavedPageLinksLeaveTheLoginOut() {
            var replaces = new List<ReplaceInfo>();
            General.AddOtherReplaces(new HTMLParser("<a href=\"2\">n</a><img src=\"i.jpg\"><link href=\"c.css\">"), PageWithLogin, replaces);

            List<string> links = replaces.Select(r => r.Value).Where(v => v.Contains("boards.example.test")).ToList();
            Assert.HasCount(3, links);
            foreach (string link in links) {
                Assert.DoesNotContain("pw-7Qz", link);
                Assert.DoesNotContain("user@", link);
            }
        }

        [TestMethod]
        public void LiveFileLinksLeaveTheLoginOut() {
            Assert.AreEqual("href=\"http://boards.example.test/a/thread/x.jpg\"", ThreadWatcher.GetLiveFileAttribute("href=\"x.jpg\"", PageWithLogin));
        }
    }
}
