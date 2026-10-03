using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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

        // B11
        [TestMethod]
        public void TranslateWebExceptionToleratesProtocolErrorWithoutResponse() {
            var ex = new WebException("no response", null, WebExceptionStatus.ProtocolError, null);

            Assert.AreSame(ex, General.TranslateWebException(ex));
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

        // The error response is closed during translation; the next request in the same group
        // (one connection allowed) must still get through.
        [TestMethod]
        public void NotFoundIsTranslatedAndNextRequestInGroupProceeds() {
            using (var server = new LoopbackHttpServer { KeepAlive = true }) {
                LoopbackResponse notFound = LoopbackResponse.StatusOnly(404, "Not Found");
                // Large enough that the framework does not drain it by itself
                notFound.Body = new byte[4000000];
                server.Route("/missing", notFound);
                server.Route("/ok", LoopbackResponse.Text("ok"));
                ServicePointManager.FindServicePoint(new Uri(server.BaseURL())).ConnectionLimit = 1;
                string group = NewGroup();

                DownloadProbe missing = DownloadProbe.Start(server.URL("/missing"), group);
                missing.AssertEndsOnce(Promptly);
                Assert.IsInstanceOfType<HTTP404Exception>(missing.Error);

                DownloadProbe next = DownloadProbe.Start(server.URL("/ok"), group);
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
                    Action abort = General.DownloadAsync(server.URL("/slow"), null, null, null, null, r => { }, (b, n) => { }, () => { },
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

                DownloadProbe probe = DownloadProbe.Start(server.URL("/start"), NewGroup());

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

        [TestMethod]
        public void ThrowingOnCompleteIsFollowedByOneOnException() {
            using (var server = new LoopbackHttpServer()) {
                server.Route("/file", LoopbackResponse.Text("ok"));
                var failure = new InvalidOperationException("corrupt");
                var done = new ManualResetEvent(false);
                int completes = 0;
                int exceptions = 0;
                Exception error = null;

                General.DownloadAsync(server.URL("/file"), null, null, null, null, r => { }, (b, n) => { },
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

        // BeginGetResponse blocks during the DNS lookup and proxy detection; a proxy that
        // doesn't answer until released stands in for a slow lookup. The abort delegate must
        // be handed back, and work, before the lookup ends.
        [TestMethod]
        public void AbortDuringSlowLookupEndsPromptly() {
            IWebProxy defaultProxy = WebRequest.DefaultWebProxy;
            using (var release = new ManualResetEvent(false)) {
                try {
                    WebRequest.DefaultWebProxy = new BlockingProxy(release);
                    DownloadProbe probe = null;
                    var starter = new Thread(() => probe = DownloadProbe.Start("http://slow-lookup.invalid/b/res/1.html"));
                    starter.Start();
                    Assert.IsTrue(starter.Join(Promptly), "DownloadAsync did not return until the lookup ended");

                    probe.Abort();

                    probe.AssertEndsOnce(Promptly);
                    Assert.AreEqual("Download has been aborted.", probe.Error.Message);
                }
                finally {
                    release.Set();
                    WebRequest.DefaultWebProxy = defaultProxy;
                }
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
                    DownloadProbe probe = DownloadProbe.Start(server.URL("/start"), NewGroup());
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

        // R2
        [TestMethod]
        public void HandleUIExceptionShowsOneMessageWhileOneIsOpen() {
            int shown = 0;
            Action<string> show = null;
            show = message => {
                shown++;
                // A recurring exception while the box is open re-enters the handler
                Program.HandleUIException(new InvalidOperationException("again"), show);
            };

            Program.HandleUIException(new InvalidOperationException("first"), show);

            Assert.AreEqual(1, shown);
        }

        [TestMethod]
        public void HandleUIExceptionSurvivesAFailingMessageAndShowsTheNextOne() {
            int shown = 0;
            Action<string> show = message => {
                shown++;
                throw new InvalidOperationException("cannot show");
            };

            Program.HandleUIException(new InvalidOperationException("first"), show);
            Program.HandleUIException(new InvalidOperationException("second"), show);

            Assert.AreEqual(2, shown);
        }

        [TestMethod]
        public void FormatUnhandledExceptionIncludesSourceAndDetails() {
            string text = Program.FormatUnhandledException("UI thread", new InvalidOperationException("boom"), true);

            StringAssert.Contains(text, "UI thread");
            StringAssert.Contains(text, "terminating");
            StringAssert.Contains(text, "System.InvalidOperationException: boom");
        }

        [TestMethod]
        public void FormatUnhandledExceptionHandlesMissingOrForeignObjects() {
            StringAssert.Contains(Program.FormatUnhandledException("Background thread", null, false), "(no exception object)");
            StringAssert.Contains(Program.FormatUnhandledException("Background thread", "thrown string", false), "thrown string");
            Assert.IsFalse(Program.FormatUnhandledException("Background thread", null, false).Contains("terminating"));
        }

        private static string NewGroup() {
            return Guid.NewGuid().ToString("N");
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

        private sealed class BlockingProxy : IWebProxy {
            private readonly WaitHandle _release;

            public BlockingProxy(WaitHandle release) {
                _release = release;
            }

            public ICredentials Credentials { get; set; }

            public Uri GetProxy(Uri destination) {
                _release.WaitOne(TimeSpan.FromSeconds(30));
                return destination;
            }

            public bool IsBypassed(Uri host) {
                _release.WaitOne(TimeSpan.FromSeconds(30));
                return true;
            }
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

            public static DownloadProbe Start(string url, string connectionGroupName = null) {
                var probe = new DownloadProbe();
                probe.Abort = General.DownloadAsync(url, null, null, connectionGroupName, null,
                    probe.OnResponse, probe.OnChunk, probe.OnComplete, probe.OnException);
                return probe;
            }

            private void OnResponse(HttpWebResponse response) {
                ProbeHeader = response.Headers["X-Probe"] + "|" + response.ContentType + "|" + response.ContentLength;
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
