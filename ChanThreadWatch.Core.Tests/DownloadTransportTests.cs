using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // B10, B11, R9, S4 (transport), R2 (formatting part)
    [TestClass]
    public class DownloadTransportTests {
        // Old behavior waited 60 s to 300 s in these cases, so 10 s separates fixed from broken with a wide margin
        private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ShortTimeoutBound = TimeSpan.FromSeconds(15);
        private const int ShortTimeoutMS = 1000;
        private const int SmallMaxPageBytes = 64 * 1024;

        [ClassInitialize]
        public static void LoadEmptySettings(TestContext context) {
            // Keep the settings store in the test output folder (no settings file there) instead of AppData
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
        }

        [TestCleanup]
        public void RestoreLimits() {
            General.RequestTimeoutMS = General.DefaultRequestTimeoutMS;
            General.ReadTimeoutMS = General.DefaultReadTimeoutMS;
            General.MaxPageBytes = General.DefaultMaxPageBytes;
            General.MaxPageReadMS = General.DefaultMaxPageReadMS;
            General.FileReadGraceMS = General.DefaultFileReadGraceMS;
            General.MinFileBytesPerSecond = General.DefaultMinFileBytesPerSecond;
            Settings.MaximumBytesPerSecond = null;
        }

        // B11: each status gets its exception, the status text is kept, and the response is released
        [TestMethod]
        public void TranslateErrorResponseMapsTheStatusAndDisposesTheResponse() {
            Assert.IsInstanceOfType<HTTP404Exception>(General.TranslateErrorResponse(ErrorResponse(404, "Not Found", null)));
            Assert.IsInstanceOfType<HTTP304Exception>(General.TranslateErrorResponse(ErrorResponse(304, "Not Modified", null)));
            HTTPRateLimitedException limited = (HTTPRateLimitedException)General.TranslateErrorResponse(ErrorResponse(503, "Service Unavailable", "30"));
            Assert.AreEqual("boards.example.org", limited.Host);
            Assert.AreEqual(TimeSpan.FromSeconds(30), limited.RetryAfter);
            HttpResponseMessage forbidden = ErrorResponse(403, "Forbidden", null);
            var status = (HTTPStatusException)General.TranslateErrorResponse(forbidden);
            Assert.AreEqual("HTTP 403 Forbidden", status.Message);
            Assert.AreEqual(403, status.StatusCode);
            Assert.AreEqual("HTTP 503 Service Unavailable", General.TranslateErrorResponse(ErrorResponse(503, "Service Unavailable", null)).Message);
            Assert.ThrowsExactly<ObjectDisposedException>(() => forbidden.Content.ReadAsStream());
        }

        // A redirect from https to http is not followed (a downgrade); the redirect answer is the result
        [TestMethod]
        [DataRow("https://a.example.org/x", "http://a.example.org/y", null)]
        [DataRow("http://a.example.org/x", "file:///C:/Windows/win.ini", null)]
        [DataRow("http://a.example.org/x", "ftp://a.example.org/y", null)]
        [DataRow("https://a.example.org/x", "https://b.example.org/y", "https://b.example.org/y")]
        [DataRow("http://a.example.org/x", "https://a.example.org/y", "https://a.example.org/y")]
        [DataRow("http://a.example.org/x/1", "../y?q=1", "http://a.example.org/y?q=1")]
        public void RedirectTarget(string requestURL, string location, string expected) {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestURL) };
            response.Headers.TryAddWithoutValidation("Location", location);

            Assert.AreEqual(expected, General.GetRedirectTarget(response)?.AbsoluteUri);
        }

        // A meta refresh is checked like a redirect: only http(s), and no downgrade from https to http
        [TestMethod]
        [DataRow("https://a.example.org/x", "http://a.example.org/y", null)]
        [DataRow("https://a.example.org/x", "https://b.example.org/y", "https://b.example.org/y")]
        [DataRow("http://a.example.org/x", "https://a.example.org/y", "https://a.example.org/y")]
        [DataRow("http://a.example.org/x", "http://b.example.org/y", "http://b.example.org/y")]
        [DataRow("http://a.example.org/x", "ftp://a.example.org/y", null)]
        public void MetaRefreshTarget(string pageURL, string redirectURL, string expected) {
            if (expected == null) {
                Assert.ThrowsExactly<NotSupportedException>(() => General.GetMetaRefreshTarget(new Uri(pageURL), redirectURL));
                return;
            }
            Assert.AreEqual(expected, General.GetMetaRefreshTarget(new Uri(pageURL), redirectURL).AbsoluteUri);
        }

        // A connection attempt is not canceled with its request, so it has a limit of its own: a
        // server that accepts the connection but never answers the TLS handshake is let go within it
        [TestMethod]
        public void AStalledConnectionAttemptEndsWithinTheRequestTimeout() {
            General.RequestTimeoutMS = ShortTimeoutMS;
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try {
                using (SocketsHttpHandler handler = General.CreateHttpHandler(TimeSpan.Zero, false))
                using (var client = new HttpClient(handler)) {
                    Assert.AreEqual(TimeSpan.FromMilliseconds(ShortTimeoutMS), handler.ConnectTimeout);
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var request = General.BuildWebRequest(new Uri("https://127.0.0.1:" + port + "/x"), null, null, null, false);
                    // The request itself gives up at once; only the handler's limit ends the attempt
                    using (var canceled = new CancellationTokenSource(100)) {
                        Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, canceled.Token)).GetAwaiter().GetResult();
                    }
                    using (System.Net.Sockets.TcpClient accepted = listener.AcceptTcpClient()) {
                        accepted.ReceiveTimeout = (int)Promptly.TotalMilliseconds;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        try {
                            var buffer = new byte[4096];
                            while (accepted.GetStream().Read(buffer, 0, buffer.Length) > 0) { }
                        }
                        catch (IOException) { }
                        Assert.IsLessThan(ShortTimeoutMS * 4, clock.ElapsedMilliseconds, "The stalled connection attempt was not given up");
                    }
                }
            }
            finally {
                listener.Stop();
            }
        }
        private static HttpResponseMessage ErrorResponse(int status, string reason, string retryAfter) {
            var response = new HttpResponseMessage((HttpStatusCode)status) {
                ReasonPhrase = reason,
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://boards.example.org/a/thread/1"),
                Content = new ByteArrayContent(new byte[10])
            };
            if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }

        [TestMethod]
        [DataRow("garbage\r\n\r\n")]
        [DataRow("HTTP/1.1 404 Not Found\r\n")]
        [DataRow("HTTP/1.1 500 Internal Server Error\r\n")]
        [DataRow("")]
        public void MalformedOrTruncatedResponseEndsInOneException(string raw) {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/x", LoopbackResponse.Raw(raw));

                DownloadProbe probe = DownloadProbe.Start(server.URL("/x"));

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Exceptions);
            }
        }

        // The error response is closed during translation; the next request to the host must still
        // get through, although the error body was too large to be drained
        [TestMethod]
        public void NotFoundIsTranslatedAndNextRequestProceeds() {
            using (var server = new LoopbackHttpServer { KeepAlive = true }) {
                LoopbackResponse notFound = LoopbackResponse.StatusOnly(404, "Not Found");
                // Larger than SocketsHttpHandler drains (1 MB), so its connection is closed
                notFound.Body = new byte[4000000];
                server.Route("/missing", notFound);
                server.Route("/ok", LoopbackResponse.Text("ok"));
                DownloadProbe missing = DownloadProbe.Start(server.URL("/missing"));
                missing.AssertEndsOnce(Promptly);
                Assert.IsInstanceOfType<HTTP404Exception>(missing.Error);

                DownloadProbe next = DownloadProbe.Start(server.URL("/ok"));
                next.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, next.Completes, next.Error?.ToString());
            }
        }

        // Abort runs AbortInternal on a thread pool thread, where an escaping exception ends the process
        [TestMethod]
        public void ThrowingOnExceptionCallbackDoesNotEscapeAbort() {
            bool unhandled = false;
            UnhandledExceptionEventHandler handler = (s, e) => unhandled = true;
            AppDomain.CurrentDomain.UnhandledException += handler;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/slow", StallBeforeResponse(release));
                    var called = new ManualResetEvent(false);
                    Action abort = General.DownloadAsync(server.URL("/slow"), null, null, false, false, null, r => { }, (b, n) => { }, () => { },
                        ex => { called.Set(); throw new InvalidOperationException("callback failure"); });

                    abort();

                    Assert.IsTrue(called.WaitOne(Promptly));
                    Thread.Sleep(500);
                    Assert.IsFalse(unhandled);
                }
                finally {
                    release.Set();
                    AppDomain.CurrentDomain.UnhandledException -= handler;
                }
            }
        }

        // B10
        [TestMethod]
        public void MetaRefreshReleasesTheReplacedConnection() {
            using (var server = new LoopbackHttpServer { KeepAlive = true }) {
                server.Route("/start", MetaRefreshResponse(server.URL("/next")));
                server.Route("/next", LoopbackResponse.Text("ok"));

                DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Completes, probe.Error?.ToString());
                Assert.AreEqual("ok", probe.BodyText);
                Assert.AreEqual(1, server.ConnectionCount, "The redirect did not reuse the connection of the replaced response");
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void HtmlDownloadReleasesItsThrottleSlot(bool redirect) {
            Settings.MaximumBytesPerSecond = 1L << 40;
            using (var server = new LoopbackHttpServer()) {
                server.Route("/start", redirect ? MetaRefreshResponse(server.URL("/next")) : LoopbackResponse.Html("<html></html>"));
                server.Route("/next", LoopbackResponse.Text("ok"));
                int before = ConcurrentDownloads();

                DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Completes, probe.Error?.ToString());
                Assert.IsTrue(SpinWait.SpinUntil(() => ConcurrentDownloads() == before, Promptly),
                    "Throttle slots still held: " + (ConcurrentDownloads() - before));
            }
        }

        [TestMethod]
        public void HtmlResponsePropertiesStayReadableInOnResponse() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/page", LoopbackResponse.Html("<html>page</html>").WithHeader("X-Probe", "yes"));

                DownloadProbe probe = DownloadProbe.Start(server.URL("/page"));

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Completes, probe.Error?.ToString());
                Assert.AreEqual("yes|text/html; charset=utf-8|17", probe.ProbeHeader);
                Assert.AreEqual("<html>page</html>", probe.BodyText);
            }
        }

        // R9
        [TestMethod]
        public void AbortDuringStalledHtmlBodyEndsPromptly() {
            // Counts the buffering stream as a download, so its release shows the blocked reader ended
            Settings.MaximumBytesPerSecond = 1L << 40;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", StalledHtml(release));
                    int before = ConcurrentDownloads();
                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));
                    WaitForRequests(server, 1);
                    Thread.Sleep(300);

                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(1, probe.Exceptions);
                    Assert.IsTrue(SpinWait.SpinUntil(() => ConcurrentDownloads() == before, Promptly),
                        "The blocked page read did not end and release its throttle slot");
                }
                finally {
                    release.Set();
                }
            }
        }

        [TestMethod]
        public void AbortWithLowSpeedLimitEndsPromptly() {
            // Throttle only sleeps once the clock has ticked since the stream was created; the body is
            // far too large to arrive before that, and the first sleep then lasts many seconds
            Settings.MaximumBytesPerSecond = 500;
            using (var server = new LoopbackHttpServer()) {
                server.Route("/file", LoopbackResponse.Bytes(new byte[32 * 1024 * 1024]));
                DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));
                Assert.IsTrue(probe.Responded.WaitOne(Promptly));
                Thread.Sleep(300);

                probe.Abort();

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Exceptions);
            }
        }

        // A read that completes synchronously (the data is already buffered, which the speed limit's
        // sleeps make likely) must not nest the next read on the stack; with HttpWebRequest's
        // BeginRead callbacks it nested one level per chunk until the stack overflowed.
        [TestMethod]
        public void ThrottledLargeDownloadDoesNotNestReads() {
            Settings.MaximumBytesPerSecond = 4 * 1024 * 1024;
            byte[] body = new byte[4 * 1024 * 1024];
            new Random(1).NextBytes(body);
            using (var server = new LoopbackHttpServer()) {
                server.Route("/file", LoopbackResponse.Bytes(body));
                var received = new MemoryStream();
                var done = new ManualResetEvent(false);
                int minDepth = int.MaxValue;
                int maxDepth = 0;
                Exception error = null;

                General.DownloadAsync(server.URL("/file"), null, null, false, false, null, r => { },
                    (b, n) => {
                        int depth = new System.Diagnostics.StackTrace().FrameCount;
                        minDepth = Math.Min(minDepth, depth);
                        maxDepth = Math.Max(maxDepth, depth);
                        received.Write(b, 0, n);
                    },
                    () => done.Set(),
                    ex => { error = ex; done.Set(); });

                Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download did not end");
                Assert.IsNull(error, error?.ToString());
                // Chunks are delivered one at a time while holding the download's lock
                Assert.IsTrue(maxDepth - minDepth < 50, "Reads nested: stack depth went from " + minDepth + " to " + maxDepth + " frames");
                CollectionAssert.AreEqual(body, received.ToArray());
            }
        }

        [TestMethod]
        public void ThrowingOnCompleteIsFollowedByOneOnException() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/file", LoopbackResponse.Text("ok"));
                var failure = new InvalidOperationException("corrupt");
                var done = new ManualResetEvent(false);
                int completes = 0;
                int exceptions = 0;
                Exception error = null;

                General.DownloadAsync(server.URL("/file"), null, null, false, false, null, r => { }, (b, n) => { },
                    () => { Interlocked.Increment(ref completes); throw failure; },
                    ex => { error = ex; Interlocked.Increment(ref exceptions); done.Set(); });

                Assert.IsTrue(done.WaitOne(Promptly));
                Thread.Sleep(300);
                Assert.AreEqual(1, Volatile.Read(ref completes));
                Assert.AreEqual(1, Volatile.Read(ref exceptions));
                Assert.AreSame(failure, error);
            }
        }

        [TestMethod]
        public void AbortDuringStalledMetaRefreshRequestEndsPromptly() {
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", MetaRefreshResponse(server.URL("/slow")));
                    server.Route("/slow", StallBeforeResponse(release));
                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));
                    WaitForRequests(server, 2);
                    Thread.Sleep(300);

                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(1, probe.Exceptions);
                }
                finally {
                    release.Set();
                }
            }
        }

        [TestMethod]
        public void StalledHtmlBodyTimesOut() {
            General.ReadTimeoutMS = ShortTimeoutMS;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", StalledHtml(release));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                    probe.AssertEndsOnce(ShortTimeoutBound);
                    Assert.AreEqual(1, probe.Exceptions);
                }
                finally {
                    release.Set();
                }
            }
        }

        // Each read gets a byte well within the read timeout, so only the deadline for the whole
        // page ends this download
        [TestMethod]
        public void DripFedHtmlBodyTimesOut() {
            General.MaxPageReadMS = ShortTimeoutMS;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", LoopbackResponse.RawThenDrip(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n<html><head>", release, TimeSpan.FromMilliseconds(100)));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                    probe.AssertEndsOnce(ShortTimeoutBound);
                    Assert.AreEqual("Timed out while reading response.", probe.Error.Message);
                }
                finally {
                    release.Set();
                }
            }
        }

        // The speed limit makes this page take about 4 s, longer than the deadline, which then
        // does not apply. The page arrives in pieces so that the throttle sleeps between reads.
        [TestMethod]
        public void SpeedLimitedHtmlPageIsNotCutOffByTheDeadline() {
            const int pageBytes = 200 * 1024;
            General.MaxPageReadMS = ShortTimeoutMS;
            Settings.MaximumBytesPerSecond = 50 * 1024;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/page", LoopbackResponse.RawThenDrip(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + pageBytes + "\r\nConnection: close\r\n\r\n",
                        release, TimeSpan.FromMilliseconds(20), 8 * 1024));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/page"));

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(new string('a', pageBytes), probe.BodyText);
                }
                finally {
                    release.Set();
                }
            }
        }

        // A speed limit no longer turns the page deadline off: this limit is far above the drip
        // rate, so the stream never sleeps and the drip still counts against the deadline
        [TestMethod]
        public void DripFedHtmlBodyTimesOutWithASpeedLimit() {
            General.MaxPageReadMS = ShortTimeoutMS;
            Settings.MaximumBytesPerSecond = 1L << 40;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", LoopbackResponse.RawThenDrip(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n<html><head>", release, TimeSpan.FromMilliseconds(100)));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                    probe.AssertEndsOnce(ShortTimeoutBound);
                    Assert.AreEqual("Timed out while reading response.", probe.Error.Message);
                }
                finally {
                    release.Set();
                }
            }
        }

        // A byte every 100 ms is far below the minimum average speed, so the file download
        // times out after the grace period although each read gets data in time
        [TestMethod]
        public void DripFedFileTimesOut() {
            General.FileReadGraceMS = ShortTimeoutMS;
            General.MinFileBytesPerSecond = 1000 * 1000;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/file", LoopbackResponse.RawThenDrip(
                        "HTTP/1.1 200 OK\r\nContent-Type: video/webm\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n", release, TimeSpan.FromMilliseconds(100)));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));

                    probe.AssertEndsOnce(ShortTimeoutBound);
                    Assert.AreEqual("Timed out while reading response.", probe.Error.Message);
                }
                finally {
                    release.Set();
                }
            }
        }

        // The speed limit makes this file take about 4 s, far longer than the 1.2 s it is
        // allowed, but the time the throttle sleeps does not count against the deadline
        [TestMethod]
        public void SpeedLimitedFileIsNotCutOffByTheDeadline() {
            const int fileBytes = 200 * 1024;
            General.FileReadGraceMS = ShortTimeoutMS;
            General.MinFileBytesPerSecond = 1000 * 1000;
            Settings.MaximumBytesPerSecond = 50 * 1024;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/file", LoopbackResponse.RawThenDrip(
                        "HTTP/1.1 200 OK\r\nContent-Type: video/webm\r\nContent-Length: " + fileBytes + "\r\nConnection: close\r\n\r\n",
                        release, TimeSpan.FromMilliseconds(20), 8 * 1024));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(1, probe.Completes, probe.Error?.ToString());
                    Assert.AreEqual(new string('a', fileBytes), probe.BodyText);
                }
                finally {
                    release.Set();
                }
            }
        }

        // A DNS lookup that ignores cancellation and doesn't answer until released stands in for a
        // slow lookup. The abort delegate must be handed back, and work, before the lookup ends.
        [TestMethod]
        public void AbortDuringSlowLookupEndsPromptly() {
            using (var release = new ManualResetEvent(false))
            using (var lookupStarted = new ManualResetEvent(false)) {
                try {
                    SSRFGuard.ResolveHost = (host, cancellationToken) => System.Threading.Tasks.Task.Run(new Func<IPAddress[]>(() => {
                        lookupStarted.Set();
                        release.WaitOne(TimeSpan.FromSeconds(30));
                        throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
                    }));
                    DownloadProbe probe = null;
                    var starter = new Thread(() => probe = DownloadProbe.Start("http://slow-lookup.invalid/b/res/1.html"));
                    starter.Start();
                    Assert.IsTrue(starter.Join(Promptly), "DownloadAsync did not return until the lookup ended");
                    Assert.IsTrue(lookupStarted.WaitOne(Promptly), "The lookup did not start");

                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual("Download has been aborted.", probe.Error.Message);
                }
                finally {
                    release.Set();
                    SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
                }
            }
        }

        // The read loop is iterative: a large body read in many small pieces through the speed limit
        // (ThrottledStream) completes, where recursion per read could overflow the stack
        [TestMethod]
        public void LargeBodyWithASpeedLimitCompletes() {
            const int fileBytes = 16 * 1024 * 1024;
            Settings.MaximumBytesPerSecond = 1L << 40;
            using (var server = new LoopbackHttpServer()) {
                byte[] body = new byte[fileBytes];
                new Random(5).NextBytes(body);
                server.Route("/file", LoopbackResponse.Bytes(body));

                DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));

                probe.AssertEndsOnce(TimeSpan.FromSeconds(60));
                Assert.AreEqual(1, probe.Completes, probe.Error?.ToString());
                CollectionAssert.AreEqual(body, probe.Body);
            }
        }

        [TestMethod]
        public void StalledMetaRefreshRequestTimesOut() {
            General.RequestTimeoutMS = ShortTimeoutMS;
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/start", MetaRefreshResponse(server.URL("/slow")));
                    server.Route("/slow", StallBeforeResponse(release));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));

                    probe.AssertEndsOnce(ShortTimeoutBound);
                    Assert.AreEqual(1, probe.Exceptions);
                }
                finally {
                    release.Set();
                }
            }
        }

        // S4
        [TestMethod]
        [DataRow(0, true)]
        [DataRow(1, false)]
        public void DownloadAsyncLimitsBufferedHtmlPageSize(int bytesOverMax, bool succeeds) {
            General.MaxPageBytes = SmallMaxPageBytes;
            using (var server = new LoopbackHttpServer()) {
                string html = HtmlOfSize(SmallMaxPageBytes + bytesOverMax);
                server.Route("/page", LoopbackResponse.Html(html));

                DownloadProbe probe = DownloadProbe.Start(server.URL("/page"));

                probe.AssertEndsOnce(Promptly);
                if (succeeds) {
                    Assert.AreEqual(html, probe.BodyText);
                }
                else {
                    Assert.IsInstanceOfType<PageTooLargeException>(probe.Error);
                }
            }
        }

        [TestMethod]
        public void DownloadPageToStringAcceptsPageAtMax() {
            General.MaxPageBytes = SmallMaxPageBytes;
            using (var server = new LoopbackHttpServer()) {
                string html = HtmlOfSize(SmallMaxPageBytes);
                server.Route("/page", LoopbackResponse.Html(html));

                Assert.AreEqual(html, General.DownloadPageToString(server.URL("/page")));
            }
        }

        [TestMethod]
        public void DownloadPageToStringRejectsPageOverMax() {
            General.MaxPageBytes = SmallMaxPageBytes;
            using (var server = new LoopbackHttpServer()) {
                server.Route("/page", LoopbackResponse.Html(HtmlOfSize(SmallMaxPageBytes + 1)));

                Assert.ThrowsExactly<PageTooLargeException>(() => General.DownloadPageToString(server.URL("/page")));
            }
        }

        // Exactly-once callbacks under abort races
        [TestMethod]
        public void AbortBeforeResponseEndsOnce() {
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/slow", StallBeforeResponse(release));

                    DownloadProbe probe = DownloadProbe.Start(server.URL("/slow"));
                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(1, probe.Exceptions);
                }
                finally {
                    release.Set();
                }
            }
        }

        [TestMethod]
        public void AbortDuringReadEndsOnce() {
            using (var release = new ManualResetEvent(false))
            using (var server = new LoopbackHttpServer()) {
                try {
                    server.Route("/file", LoopbackResponse.RawThenStall("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n0123456789", release));
                    DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));
                    Assert.IsTrue(probe.Responded.WaitOne(Promptly));

                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual(1, probe.Exceptions);
                }
                finally {
                    release.Set();
                }
            }
        }

        [TestMethod]
        public void AbortAfterCompleteDoesNotCallOnException() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/file", LoopbackResponse.Text("ok"));
                DownloadProbe probe = DownloadProbe.Start(server.URL("/file"));
                Assert.IsTrue(probe.Done.WaitOne(Promptly));

                probe.Abort();

                probe.AssertEndsOnce(Promptly);
                Assert.AreEqual(1, probe.Completes);
            }
        }

        [TestMethod]
        public void AbortAtRandomTimesEndsOnceEveryTime() {
            var random = new Random(1234);
            var probes = new List<DownloadProbe>();
            using (var server = new LoopbackHttpServer()) {
                server.Route("/start", MetaRefreshResponse(server.URL("/next")));
                server.Route("/next", LoopbackResponse.Bytes(new byte[200000]));

                for (int i = 0; i < 40; i++) {
                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"));
                    Thread.Sleep(random.Next(0, 20));
                    probe.Abort();
                    Assert.IsTrue(probe.Done.WaitOne(Promptly), "Download " + i + " never ended");
                    probes.Add(probe);
                }

                Thread.Sleep(500);
                for (int i = 0; i < probes.Count; i++) {
                    Assert.AreEqual(1, probes[i].Callbacks, "Download " + i + " ended " + probes[i].Callbacks + " times");
                }
            }
        }

        private static string HtmlOfSize(int size) {
            const string head = "<html><body>";
            return head + new string('a', size - head.Length);
        }

        private static LoopbackResponse MetaRefreshResponse(string url) {
            return LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head></html>");
        }

        // Headers and the start of an HTML body, then nothing until released
        private static LoopbackResponse StalledHtml(WaitHandle release) {
            return LoopbackResponse.RawThenStall("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n<html><head>", release);
        }

        // No response at all until released
        private static Func<RecordedRequest, LoopbackResponse> StallBeforeResponse(WaitHandle release) {
            return r => {
                release.WaitOne(TimeSpan.FromSeconds(30));
                return LoopbackResponse.Text("late");
            };
        }

        private static void WaitForRequests(LoopbackHttpServer server, int count) {
            Assert.IsTrue(SpinWait.SpinUntil(() => server.Requests.Count >= count, Promptly), "Server did not receive " + count + " requests");
        }

        private static int ConcurrentDownloads() {
            FieldInfo field = typeof(ThrottledStream).GetField("_concurrentDownloads", BindingFlags.NonPublic | BindingFlags.Static);
            return (int)field.GetValue(null);
        }

        private sealed class DownloadProbe {
            private readonly MemoryStream _body = new MemoryStream();
            private int _completes;
            private int _exceptions;

            public ManualResetEvent Done { get; } = new ManualResetEvent(false);
            public ManualResetEvent Responded { get; } = new ManualResetEvent(false);
            public Exception Error { get; private set; }
            public string ProbeHeader { get; private set; }
            public Action Abort { get; private set; }

            public int Completes => Volatile.Read(ref _completes);
            public int Exceptions => Volatile.Read(ref _exceptions);
            public int Callbacks => Completes + Exceptions;

            public string BodyText {
                get { lock (_body) return Encoding.UTF8.GetString(_body.ToArray()); }
            }

            public byte[] Body {
                get { lock (_body) return _body.ToArray(); }
            }

            public static DownloadProbe Start(string url) {
                var probe = new DownloadProbe();
                probe.Abort = General.DownloadAsync(url, null, null, false, false, null,
                    probe.OnResponse, probe.OnChunk, probe.OnComplete, probe.OnException);
                return probe;
            }

            private void OnResponse(HttpResponseMessage response) {
                IEnumerable<string> probe;
                ProbeHeader = (response.Headers.TryGetValues("X-Probe", out probe) ? String.Join(",", probe) : null) + "|" + response.Content.Headers.ContentType + "|" + response.Content.Headers.ContentLength;
                Responded.Set();
            }

            private void OnChunk(byte[] data, int length) {
                lock (_body) _body.Write(data, 0, length);
            }

            private void OnComplete() {
                Interlocked.Increment(ref _completes);
                Done.Set();
            }

            private void OnException(Exception ex) {
                Error = ex;
                Interlocked.Increment(ref _exceptions);
                Done.Set();
            }

            // Waits for the final callback, then gives a wrong second callback time to show up
            public void AssertEndsOnce(TimeSpan within) {
                Assert.IsTrue(Done.WaitOne(within), "Download did not end within " + within);
                Thread.Sleep(300);
                Assert.AreEqual(1, Callbacks, "Completes: " + Completes + ", exceptions: " + Exceptions + ", error: " + Error);
            }
        }
    }
}
