using System;
using System.Net;
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

        // S1
        [TestMethod]
        public void ThreadWatcherDoesNotDisableCertificateValidation() {
            RuntimeHelpers.RunClassConstructor(typeof(ThreadWatcher).TypeHandle);

            Assert.IsNull(ServicePointManager.ServerCertificateValidationCallback);
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

        // Pins framework behavior S2 relies on: HttpWebRequest drops a manually added Authorization header on automatic redirects
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
        [DataRow("../..\\x", "....x")]
        public void CleanFileNameNeverReturnsDotSegments(string input, string expected) {
            Assert.AreEqual(expected, General.CleanFileName(input));
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

        private static readonly LoopbackResponse OkResponse = LoopbackResponse.Text("ok");

        private static LoopbackResponse MetaRefreshResponse(string url) {
            return LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head></html>");
        }

        private static void Download(string url, string auth, string referer) {
            var done = new ManualResetEvent(false);
            Exception error = null;
            General.DownloadAsync(url, auth, referer, null, null, r => { }, (b, n) => { }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download timed out");
            Assert.IsNull(error, error?.ToString());
        }
    }
}
