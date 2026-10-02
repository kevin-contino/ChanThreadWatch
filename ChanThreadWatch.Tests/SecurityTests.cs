using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
            using (var target = new LoopbackServer(OkResponse))
            using (var start = new LoopbackServer(MetaRefreshResponse("http://localhost:" + target.Port + "/x"))) {
                Download(start.URL("/start"), "user:pass", null);

                StringAssert.Contains(start.Requests[0], "Authorization: Basic");
                Assert.DoesNotContain("Authorization", target.Requests[0]);
            }
        }

        // Pins framework behavior S2 relies on: HttpWebRequest drops a manually added Authorization header on automatic redirects
        [TestMethod]
        public void HttpRedirectToOtherOriginDropsCredentials() {
            using (var target = new LoopbackServer(OkResponse))
            using (var start = new LoopbackServer("HTTP/1.1 302 Found\r\nLocation: http://localhost:" + target.Port + "/x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")) {
                Download(start.URL("/start"), "user:pass", null);

                StringAssert.Contains(start.Requests[0], "Authorization: Basic");
                Assert.DoesNotContain("Authorization", target.Requests[0]);
            }
        }

        [TestMethod]
        public void MetaRefreshToSameOriginKeepsCredentials() {
            using (var server = new LoopbackServer()) {
                server.Responses.Add(MetaRefreshResponse(server.URL("/next")));
                server.Responses.Add(OkResponse);

                Download(server.URL("/start"), "user:pass", null);

                Assert.HasCount(2, server.Requests);
                StringAssert.Contains(server.Requests[1], "Authorization: Basic");
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
            using (var server = new LoopbackServer(OkResponse)) {
                Download(server.URL("/image.jpg"), null, "https://boards.example.org/a/thread/1");

                StringAssert.Contains(server.Requests[0], "Referer: https://boards.example.org/a/thread/1");
            }
        }

        private const string OkResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";

        private static string MetaRefreshResponse(string url) {
            string body = "<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head></html>";
            return "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;
        }

        private static void Download(string url, string auth, string referer) {
            var done = new ManualResetEvent(false);
            Exception error = null;
            General.DownloadAsync(url, auth, referer, null, null, r => { }, (b, n) => { }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download timed out");
            Assert.IsNull(error, error?.ToString());
        }

        // Minimal HTTP server on 127.0.0.1 that answers each connection with the next canned response and records the raw request
        private sealed class LoopbackServer : IDisposable {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread _thread;

            public List<string> Responses { get; } = new List<string>();
            public List<string> Requests { get; } = new List<string>();

            public LoopbackServer(params string[] responses) {
                Responses.AddRange(responses);
                _listener.Start();
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public string URL(string path) => "http://127.0.0.1:" + Port + path;

            private void Serve() {
                try {
                    for (int i = 0; ; i++) {
                        using (TcpClient client = _listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream()) {
                            var buffer = new byte[16384];
                            int length = stream.Read(buffer, 0, buffer.Length);
                            lock (Requests) Requests.Add(Encoding.ASCII.GetString(buffer, 0, length));
                            byte[] response = Encoding.ASCII.GetBytes(Responses[Math.Min(i, Responses.Count - 1)]);
                            stream.Write(response, 0, response.Length);
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }

            public void Dispose() {
                _listener.Stop();
                _thread.Join(5000);
            }
        }
    }
}
