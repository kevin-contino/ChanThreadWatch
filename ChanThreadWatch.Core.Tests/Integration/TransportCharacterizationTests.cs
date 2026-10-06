using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // MP-5a: pins what the transport sends and how it reacts, as seen from the server. Written
    // against HttpWebRequest, so that the HttpClient rewrite (MP-5b) could be checked against it. Behaviors that
    // other tests already pin are not repeated here.
    [TestClass]
    public class TransportCharacterizationTests : ThreadWatcherIntegrationTestBase {
        private const string MediaHost = "localhost";
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
        private const string LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        private const int ShortTimeoutMS = 1000;
        private static readonly string FirstImage = FourChanThreadFixture.ImagePaths[0];

        private readonly ManualResetEvent _release = new ManualResetEvent(false);
        private readonly string _sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        private readonly List<string> _sentinelDirs = new List<string>();

        [TestCleanup]
        public void RestoreTransportLimits() {
            _release.Set();
            foreach (string dir in _sentinelDirs) {
                try { Directory.Delete(dir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            General.RequestTimeoutMS = General.DefaultRequestTimeoutMS;
            General.ReadTimeoutMS = General.DefaultReadTimeoutMS;
            General.MaxPageBytes = General.DefaultMaxPageBytes;
        }

        // Every request carries the browser User-Agent unless a custom one is set
        [TestMethod]
        public void EveryRequestSendsTheBrowserUserAgent() {
            LoopbackHttpServer server = StartServer();
            new FourChanThreadFixture().RouteAll(server);
            server.Route("/page", LoopbackResponse.Html("<html></html>"));

            RunToStop(CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath)));
            General.DownloadPageToString(server.URL("/page"));

            Assert.HasCount(1 + 4 + 3 + 1, server.Requests);
            foreach (RecordedRequest request in server.Requests) {
                Assert.AreEqual(BrowserUserAgent, request.Header("User-Agent"), request.ToString());
            }
        }

        [TestMethod]
        public void EveryRequestSendsTheCustomUserAgentWhenSet() {
            Settings.UseCustomUserAgent = true;
            Settings.CustomUserAgent = "CustomAgent/1.0 (test)";
            LoopbackHttpServer server = StartServer();
            new FourChanThreadFixture().RouteAll(server);
            server.Route("/page", LoopbackResponse.Html("<html></html>"));

            RunToStop(CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath)));
            General.DownloadPageToString(server.URL("/page"));

            Assert.HasCount(1 + 4 + 3 + 1, server.Requests);
            foreach (RecordedRequest request in server.Requests) {
                Assert.AreEqual("CustomAgent/1.0 (test)", request.Header("User-Agent"), request.ToString());
            }
        }

        // The complete set of request headers: no Accept, Accept-Encoding (so no compression),
        // Cookie or Authorization unless one is set up. The Connection header is left out: the
        // framework only sends it on some requests.
        [TestMethod]
        public void RequestsSendOnlyTheseHeaders() {
            LoopbackHttpServer server = StartServer();
            new FourChanThreadFixture().RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);

            RunToStop(CreateWatcher(url));

            RecordedRequest page = server.RequestsTo(FourChanThreadFixture.ThreadPath)[0];
            Assert.AreEqual("GET", page.Method);
            Assert.AreEqual("Host,User-Agent", HeaderNames(page), page.Raw);
            Assert.AreEqual("127.0.0.1:" + server.Port, page.Header("Host"), page.Raw);
            RecordedRequest image = server.RequestsTo(FirstImage)[0];
            Assert.AreEqual("GET", image.Method);
            Assert.AreEqual("Host,Referer,User-Agent", HeaderNames(image), image.Raw);
        }

        // Basic auth is "user:password" encoded as ISO-8859-1, then base64
        [TestMethod]
        public void BasicAuthIsEncodedAsLatin1() {
            LoopbackHttpServer server = StartServer();
            new FourChanThreadFixture().RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.PageAuth = "user:pässwörd";

            RunToStop(watcher);

            // 75 73 65 72 3A 70 E4 73 73 77 F6 72 64
            Assert.AreEqual("Basic dXNlcjpw5HNzd/ZyZA==", server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].Header("Authorization"));
        }

        // If-Modified-Since repeats the Last-Modified of the previous page response, in RFC 1123
        // form whatever form the server used; a 304 saves nothing and queues no files
        [TestMethod]
        [DataRow(LastModified)]
        [DataRow("Wednesday, 01-Jan-25 00:00:00 GMT")]
        [DataRow("Wed Jan  1 00:00:00 2025")]
        public void IfModifiedSinceRepeatsThePreviousLastModified(string lastModified) {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Last-Modified", lastModified)
                .WithHeader("Date", "Thu, 02 Jan 2025 00:00:00 GMT");
            server.Route(FourChanThreadFixture.ThreadPath, r => r.Header("If-Modified-Since") != null ? LoopbackResponse.StatusOnly(304, "Not Modified") : page);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            string savedPage = null;

            RunChecks(watcher, 2, check => savedPage = File.ReadAllText(SavedPagePath(watcher)));

            List<RecordedRequest> pageRequests = server.RequestsTo(FourChanThreadFixture.ThreadPath);
            Assert.HasCount(2, pageRequests);
            Assert.IsNull(pageRequests[0].Header("If-Modified-Since"));
            Assert.AreEqual(LastModified, pageRequests[1].Header("If-Modified-Since"));
            Assert.HasCount(1 + 4 + 3 + 1, server.Requests);
            Assert.AreEqual(savedPage, File.ReadAllText(SavedPagePath(watcher)));
            foreach (RecordedRequest request in server.Requests) {
                if (request.Path != FourChanThreadFixture.ThreadPath) Assert.IsNull(request.Header("If-Modified-Since"), request.ToString());
            }
        }

        // Without a usable Last-Modified, the next check requests the page unconditionally
        [TestMethod]
        [DataRow(null)]
        [DataRow("yesterday")]
        public void NoIfModifiedSinceWithoutAUsableLastModified(string lastModified) {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Date", "Thu, 02 Jan 2025 00:00:00 GMT");
            if (lastModified != null) page.WithHeader("Last-Modified", lastModified);
            server.Route(FourChanThreadFixture.ThreadPath, page);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 2);

            List<RecordedRequest> pageRequests = server.RequestsTo(FourChanThreadFixture.ThreadPath);
            Assert.HasCount(2, pageRequests);
            Assert.IsNull(pageRequests[1].Header("If-Modified-Since"));
        }

        // There is no cookie store: Set-Cookie is ignored and no request carries a Cookie
        [TestMethod]
        public void SetCookieIsIgnoredAndNoCookieIsSent() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Set-Cookie", "session=abc123; Path=/")
                .WithHeader("Set-Cookie", "other=xyz; Path=/; HttpOnly"));
            server.Route(FirstImage, LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg").WithHeader("Set-Cookie", "image=1; Path=/"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 2);

            Assert.HasCount(2, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            foreach (RecordedRequest request in server.Requests) {
                Assert.IsNull(request.Header("Cookie"), request.ToString());
            }
        }

        // A file that is redirected keeps its Referer on the redirected request
        [TestMethod]
        public void RedirectedFileRequestKeepsTheReferer() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, Redirect(302, "Found", "/moved.jpg"));
            server.Route("/moved.jpg", LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));
            string url = server.URL(FourChanThreadFixture.ThreadPath);

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(CreateWatcher(url)));

            Assert.AreEqual(url, server.RequestsTo("/moved.jpg")[0].Header("Referer"));
        }

        // A same-origin HTTP redirect drops the Authorization header too (HttpWebRequest removed it on
        // every automatic redirect, and MP-5b kept that). SecurityTests.MetaRefreshToSameOriginKeepsCredentials
        // pins the meta refresh counterpart.
        // Characterizes behavior, not a security requirement.
        [TestMethod]
        public void SameOriginHttpRedirectDropsCredentials() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()));
            server.Route(FourChanThreadFixture.ThreadPath, Redirect(302, "Found", "/moved"));
            server.Route("/moved", page);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.PageAuth = "user:pass";

            RunToStop(watcher);

            Assert.AreEqual("user:pass", server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].BasicAuth);
            Assert.IsNull(server.RequestsTo("/moved")[0].Header("Authorization"));
        }

        // HTTP redirects are followed for these status codes
        [TestMethod]
        [DataRow(301, "Moved Permanently", "ok")]
        [DataRow(302, "Found", "ok")]
        [DataRow(303, "See Other", "ok")]
        [DataRow(307, "Temporary Redirect", "ok")]
        // .NET Framework's HttpWebRequest did not follow 308 ("HTTP 308"); .NET 10's did, and MP-5b follows it (standard)
        [DataRow(308, "Permanent Redirect", "ok")]
        public void RedirectStatusesThatAreFollowed(int status, string reason, string expected) {
            LoopbackHttpServer server = StartServer();
            server.Route("/start", Redirect(status, reason, "/end"));
            server.Route("/end", LoopbackResponse.Html("ok"));

            Assert.AreEqual(expected, Outcome(() => General.DownloadPageToString(server.URL("/start"))));
            Assert.HasCount(expected == "ok" ? 1 : 0, server.RequestsTo("/end"));
        }

        // At most 50 redirects in a row are followed
        [TestMethod]
        [DataRow(50, "ok")]
        [DataRow(51, "HTTP 302")]
        public void AtMostFiftyRedirectsAreFollowed(int redirects, string expected) {
            LoopbackHttpServer server = StartServer();
            for (int i = redirects; i > 0; i--) {
                server.Route("/r" + i, Redirect(302, "Found", "/r" + (i - 1)));
            }
            server.Route("/r0", LoopbackResponse.Html("ok"));

            Assert.AreEqual(expected, Outcome(() => General.DownloadPageToString(server.URL("/r" + redirects))));
            Assert.HasCount(Math.Min(redirects, 50) + 1, server.Requests);
        }

        // After an HTTP redirect of the thread page, the page is saved under the URL that was
        // asked for, and its relative links resolve against that URL, not the redirect target
        [TestMethod]
        public void RelativeLinksResolveAgainstTheRequestedURLAfterARedirect() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteImages(server);
            fixture.RouteThumbs(server);
            server.Route(FourChanThreadFixture.ThreadPath, Redirect(302, "Found", server.URL("/moved", MediaHost)));
            server.Route("/moved", LoopbackResponse.Html(fixture.Html(String.Empty, String.Empty)));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(MediaHost + ":" + server.Port, server.RequestsTo("/moved")[0].Header("Host"));
            Assert.AreEqual("127.0.0.1:" + server.Port, server.RequestsTo(FirstImage)[0].Header("Host"));
            Assert.AreEqual(server.URL(FourChanThreadFixture.ThreadPath), server.RequestsTo(FirstImage)[0].Header("Referer"));
            Assert.IsTrue(File.Exists(SavedPagePath(watcher)));
        }

        // A rate limit answer at the end of an HTTP redirect pauses the host that gave it
        [TestMethod]
        public void RateLimitAfterAnHttpRedirectPausesTheRedirectTarget() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, Redirect(302, "Found", server.URL("/target", MediaHost)));
            server.Route("/target", TooManyRequests().WithHeader("Retry-After", "30"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            Assert.IsTrue(ConnectionManager.GetInstanceForHost(MediaHost).IsPaused);
            Assert.IsFalse(ConnectionManager.GetInstanceForHost(PageHost).IsPaused);
            Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
            Assert.HasCount(1, server.RequestsTo("/target"));
        }

        // With the production limits, a Retry-After under 5 s pauses for 5 s and one over 2 h
        // for 2 h
        [TestMethod]
        [DataRow("1", 5000)]
        [DataRow("0", 5000)]
        [DataRow("86400", 2 * 60 * 60 * 1000)]
        public void RetryAfterIsClampedToTheProductionLimits(string retryAfter, int expectedPauseMS) {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteThread(server, server.BaseURL(MediaHost), server.BaseURL());
            fixture.RouteImages(server);
            fixture.RouteThumbs(server);
            server.Route(FirstImage, TooManyRequests().WithHeader("Retry-After", retryAfter));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            long before = TickCount.Now;
            RunChecks(watcher, 1);
            long after = TickCount.Now;

            long pausedUntil = ConnectionManager.GetInstanceForHost(MediaHost).PausedUntilTicks;
            Assert.IsTrue(before + expectedPauseMS <= pausedUntil && pausedUntil <= after + expectedPauseMS,
                "Pause ends " + (pausedUntil - before) + " ms after the check started, which took " + (after - before) + " ms");
            Assert.HasCount(1, server.RequestsTo(FirstImage));
        }

        // With the production interval, a retry starts at least one second after the request
        // before it. The starts are taken where the watcher sends each request, so the time the
        // first request spends on its way (e.g. JIT) does not shorten the measured gap.
        [TestMethod]
        public void RequestsToOneHostStartOneSecondApart() {
            ConnectionManager.MinRequestStartIntervalMS = ConnectionManager.DefaultMinRequestStartIntervalMS;
            LoopbackHttpServer server = StartServer();
            server.RouteSequence(FourChanThreadFixture.ThreadPath, LoopbackResponse.StatusOnly(500, "Internal Server Error"), LoopbackResponse.StatusOnly(404, "Not Found"));
            var clock = Stopwatch.StartNew();
            var starts = new List<long>();
            ThreadWatcher.BeforeRequestStart = url => { lock (starts) starts.Add(clock.ElapsedMilliseconds); };

            Assert.AreEqual(StopReason.PageNotFound, RunToStop(CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath))));

            Assert.HasCount(2, starts);
            Assert.IsGreaterThanOrEqualTo(950, starts[1] - starts[0]);
        }

        // A retry of the page goes out on a new connection, while requests that are not
        // retries reuse connections
        [TestMethod]
        public void PageRetryUsesAFreshConnection() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            server.KeepAlive = true;
            fixture.RouteAll(server);
            server.RouteSequence(FourChanThreadFixture.ThreadPath, LoopbackResponse.StatusOnly(500, "Internal Server Error"),
                LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL())));

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath))));

            List<RecordedRequest> pageRequests = server.RequestsTo(FourChanThreadFixture.ThreadPath);
            Assert.HasCount(2, pageRequests);
            Assert.AreNotEqual(pageRequests[0].ConnectionId, pageRequests[1].ConnectionId);
            Assert.HasCount(2 + 4 + 3, server.Requests);
            Assert.IsLessThan(server.Requests.Count, server.ConnectionCount);
        }

        // A retry of a file goes out on a new connection
        [TestMethod]
        public void FileRetryUsesAFreshConnection() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            server.KeepAlive = true;
            fixture.RouteAll(server);
            server.RouteSequence(FirstImage, LoopbackResponse.StatusOnly(500, "Internal Server Error"),
                LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath))));

            List<RecordedRequest> imageRequests = server.RequestsTo(FirstImage);
            Assert.HasCount(2, imageRequests);
            Assert.AreNotEqual(imageRequests[0].ConnectionId, imageRequests[1].ConnectionId);
        }

        // A page whose response headers never arrive times out after RequestTimeoutMS, each of the three tries
        [TestMethod]
        public void PageWithoutResponseTimesOutOnEveryTry() {
            General.RequestTimeoutMS = ShortTimeoutMS;
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, StallBeforeResponse());
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.AreEqual("Timed out while waiting for response", watcher.CheckError);
            Assert.HasCount(3, server.RequestsTo(FourChanThreadFixture.ThreadPath));
        }

        // A file whose body stops arriving times out after ReadTimeoutMS, each of the three tries
        [TestMethod]
        public void StalledFileBodyTimesOutOnEveryTry() {
            General.ReadTimeoutMS = ShortTimeoutMS;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, LoopbackResponse.RawThenStall("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: 100000\r\nConnection: close\r\n\r\nabc", _release));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(1, watcher.FailedFileCount);
            Assert.HasCount(3, server.RequestsTo(FirstImage));
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(FirstImage) + ": Timed out while reading response" + Environment.NewLine);
        }

        // The synchronous page download is bounded by the same two timeouts
        [TestMethod]
        public void DownloadPageToStringTimesOut() {
            General.RequestTimeoutMS = ShortTimeoutMS;
            General.ReadTimeoutMS = ShortTimeoutMS;
            LoopbackHttpServer server = StartServer();
            server.Route("/no-response", StallBeforeResponse());
            server.Route("/stalled-body", LoopbackResponse.RawThenStall("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n<html>", _release));

            Assert.AreEqual("timeout", Outcome(() => General.DownloadPageToString(server.URL("/no-response"))));
            Assert.AreEqual("timeout", Outcome(() => General.DownloadPageToString(server.URL("/stalled-body"))));
        }

        // A file that is not found is skipped after one request and not counted as failed
        [TestMethod]
        public void FileNotFoundIsSkippedWithoutRetry() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, LoopbackResponse.StatusOnly(404, "Not Found"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(1, server.RequestsTo(FirstImage));
            Assert.AreEqual(0, watcher.FailedFileCount);
            Assert.IsNull(watcher.CheckError);
        }

        // A page and a file sent without Content-Length (chunked) are saved in full
        [TestMethod]
        public void ChunkedPageAndFileAreSavedInFull() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] page = Encoding.UTF8.GetBytes(fixture.Html(server.BaseURL(), server.BaseURL()));
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Chunked(page, "text/html; charset=utf-8"));
            server.Route(FirstImage, LoopbackResponse.Chunked(fixture.Images[FirstImage], "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.HasCount(1, server.RequestsTo(FirstImage));
            Assert.HasCount(1 + 4 + 3, server.Requests);
            CollectionAssert.AreEqual(fixture.Images[FirstImage], File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            string savedPage = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(savedPage, "<a class=\"fileThumb\" href=\"1700000000001.jpg\"");
            // The last post and the end of the document made it into the saved page
            StringAssert.Contains(savedPage, "Text only, no file");
            StringAssert.EndsWith(savedPage.TrimEnd(), "</html>");
        }

        // The page size limit also applies to a page of unknown length
        [TestMethod]
        public void ChunkedPageOverTheSizeLimitIsReportedWithoutRetrying() {
            General.MaxPageBytes = 1024;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] page = Encoding.UTF8.GetBytes(fixture.Html(server.BaseURL(), server.BaseURL()));
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Chunked(page, "text/html; charset=utf-8"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.AreEqual("The page is larger than the maximum of 1024 bytes", watcher.StopError);
            Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
        }

        // Security (kept by MP-5b): an HTTP redirect to a scheme other than http(s) is not
        // followed, so a local file or an FTP server is never read
        [TestMethod]
        [DataRow("file")]
        [DataRow("ftp")]
        public void HttpRedirectToANonHttpSchemeIsNotFollowed(string scheme) {
            LoopbackHttpServer server = StartServer();
            string sentinelDir = CreateSentinelFolder("x");
            string target = scheme == "file" ? new Uri(Path.Combine(sentinelDir, "x")).AbsoluteUri : "ftp://127.0.0.1:" + server.Port + "/x";
            server.Route("/start", Redirect(302, "Found", target));
            string body;

            Assert.AreNotEqual("ok", AsyncOutcome(server.URL("/start"), null, out body));
            Assert.DoesNotContain(_sentinel, body);
            Assert.AreNotEqual("ok", Outcome(() => General.DownloadPageToString(server.URL("/start"))));
            Assert.AreEqual(server.Requests.Count, server.ConnectionCount, "Something other than HTTP connected to the server");
        }

        // Security (kept by MP-5b): a meta refresh to a scheme other than http(s) is not followed
        [TestMethod]
        [DataRow("file")]
        [DataRow("ftp")]
        [DataRow("javascript")]
        [DataRow("data")]
        public void MetaRefreshToANonHttpSchemeIsNotFollowed(string scheme) {
            LoopbackHttpServer server = StartServer();
            string sentinelDir = CreateSentinelFolder("x");
            string target =
                scheme == "file" ? new Uri(Path.Combine(sentinelDir, "x")).AbsoluteUri :
                scheme == "ftp" ? "ftp://127.0.0.1:" + server.Port + "/x" :
                scheme == "javascript" ? "javascript:alert(1)" :
                "data:text/plain;base64," + Convert.ToBase64String(Encoding.ASCII.GetBytes(_sentinel));
            server.Route("/start", MetaRefresh(target));
            string body;

            Assert.AreNotEqual("ok", AsyncOutcome(server.URL("/start"), null, out body));
            Assert.DoesNotContain(_sentinel, body);
            Assert.AreEqual(server.Requests.Count, server.ConnectionCount, "Something other than HTTP connected to the server");
        }

        // Security (kept by MP-5b): file and thumbnail links with a scheme other than http(s)
        // are never fetched or saved
        [TestMethod]
        [DataRow("file")]
        [DataRow("ftp")]
        public void NonHttpFileLinksAreNotFetched(string scheme) {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            string[] paths = FourChanThreadFixture.ImagePaths.Concat(FourChanThreadFixture.ThumbPaths).Select(p => p.Substring(1)).ToArray();
            string sentinelDir = CreateSentinelFolder(paths);
            string baseURL = scheme == "file" ? new Uri(sentinelDir).AbsoluteUri : "ftp://127.0.0.1:" + server.Port;
            fixture.RouteThread(server, baseURL, baseURL);
            fixture.RouteImages(server);
            fixture.RouteThumbs(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            Assert.HasCount(1, server.Requests);
            Assert.AreEqual(1, server.ConnectionCount, "Something other than HTTP connected to the server");
            AssertSentinelNotSaved();
        }

        // Security (kept by MP-5b): the auth origin is scheme, host and port, so a redirect to the
        // same host on another port gets no credentials
        [TestMethod]
        public void RedirectToAnotherPortOfTheSameHostDropsCredentials() {
            LoopbackHttpServer a = StartServer();
            LoopbackHttpServer b = StartServer();
            a.Route("/start", Redirect(302, "Found", b.URL("/x")));
            b.Route("/x", LoopbackResponse.Html("x"));
            string body;

            Assert.AreEqual("ok", AsyncOutcome(a.URL("/start"), "user:pass", out body));

            Assert.AreEqual("user:pass", a.Requests[0].BasicAuth);
            Assert.IsNull(b.Requests[0].Header("Authorization"));
        }

        // Security (kept by MP-5b): once a redirect leaves the origin, no later hop gets credentials
        [TestMethod]
        public void RedirectChainToAnotherOriginSendsNoCredentialsOnAnyHop() {
            LoopbackHttpServer a = StartServer();
            LoopbackHttpServer b = StartServer();
            a.Route("/start", Redirect(302, "Found", b.URL("/x", MediaHost)));
            b.Route("/x", Redirect(302, "Found", "/y"));
            b.Route("/y", LoopbackResponse.Html("y"));
            string body;

            Assert.AreEqual("ok", AsyncOutcome(a.URL("/start"), "user:pass", out body));

            Assert.HasCount(2, b.Requests);
            foreach (RecordedRequest request in b.Requests) {
                Assert.IsNull(request.Header("Authorization"), request.ToString());
            }
        }

        // A chain that leaves the origin and comes back to it: the return hop gets no
        // credentials either. Characterizes current behavior (the framework drops Authorization
        // on the first redirect, and MP-5b drops it on every redirect).
        [TestMethod]
        public void RedirectChainBackToTheOriginSendsNoCredentials() {
            LoopbackHttpServer a = StartServer();
            LoopbackHttpServer b = StartServer();
            a.Route("/start", Redirect(302, "Found", b.URL("/x", MediaHost)));
            b.Route("/x", Redirect(302, "Found", a.URL("/back")));
            a.Route("/back", LoopbackResponse.Html("back"));
            string body;

            Assert.AreEqual("ok", AsyncOutcome(a.URL("/start"), "user:pass", out body));

            Assert.AreEqual("user:pass", a.RequestsTo("/start")[0].BasicAuth);
            Assert.IsNull(b.Requests[0].Header("Authorization"));
            Assert.IsNull(a.RequestsTo("/back")[0].Header("Authorization"));
        }

        // Security (kept by MP-5b): an image (ImageAuth) and a thumbnail (PageAuth) redirected to
        // another origin get no credentials there
        [TestMethod]
        public void FilesRedirectedToAnotherOriginGetNoCredentials() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            LoopbackHttpServer media = StartServer();
            fixture.RouteAll(server);
            string firstThumb = FourChanThreadFixture.ThumbPaths[0];
            server.Route(FirstImage, Redirect(302, "Found", media.URL("/image", MediaHost)));
            server.Route(firstThumb, Redirect(302, "Found", media.URL("/thumb", MediaHost)));
            media.Route("/image", LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));
            media.Route("/thumb", LoopbackResponse.Bytes(fixture.Thumbs[firstThumb], "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.PageAuth = "pageuser:pagepass";
            watcher.ImageAuth = "imageuser:imagepass";

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(watcher));

            Assert.AreEqual("imageuser:imagepass", server.RequestsTo(FirstImage)[0].BasicAuth);
            Assert.AreEqual("pageuser:pagepass", server.RequestsTo(firstThumb)[0].BasicAuth);
            Assert.HasCount(1, media.RequestsTo("/image"));
            Assert.HasCount(1, media.RequestsTo("/thumb"));
            foreach (RecordedRequest request in media.Requests) {
                Assert.IsNull(request.Header("Authorization"), request.ToString());
            }
        }

        // Security (kept by MP-5b): a relative meta refresh on a page reached through an HTTP
        // redirect to another origin resolves there and gets no credentials
        [TestMethod]
        public void RelativeMetaRefreshAfterARedirectToAnotherOriginGetsNoCredentials() {
            LoopbackHttpServer a = StartServer();
            LoopbackHttpServer b = StartServer();
            a.Route("/start", Redirect(302, "Found", b.URL("/page", MediaHost)));
            b.Route("/page", MetaRefresh("/next"));
            b.Route("/next", LoopbackResponse.Html("next page"));
            string body;

            Assert.AreEqual("ok", AsyncOutcome(a.URL("/start"), "user:pass", out body));

            Assert.AreEqual("next page", body);
            Assert.HasCount(1, b.RequestsTo("/next"));
            Assert.IsNull(b.RequestsTo("/next")[0].Header("Authorization"));
            Assert.IsEmpty(a.RequestsTo("/next"));
        }

        // Only one meta refresh is followed: the page it leads to is returned as it is, even if
        // it has a meta refresh of its own
        [TestMethod]
        public void OnlyOneMetaRefreshHopIsFollowed() {
            LoopbackHttpServer server = StartServer();
            server.Route("/start", MetaRefresh("/second"));
            server.Route("/second", LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=/third\"></head><body>second page</body></html>"));
            server.Route("/third", LoopbackResponse.Html("third page"));
            string body;

            Assert.AreEqual("ok", AsyncOutcome(server.URL("/start"), null, out body));

            StringAssert.Contains(body, "second page");
            Assert.HasCount(1, server.RequestsTo("/second"));
            Assert.IsEmpty(server.RequestsTo("/third"));
        }

        // A thread URL may carry user:password@ (CleanPageURL keeps it). The login is not sent as
        // credentials, and it never reaches the other host in the Referer.
        [TestMethod]
        public void ThreadURLWithUserInfo() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            LoopbackHttpServer media = StartServer();
            string mediaBase = media.BaseURL(MediaHost);
            fixture.RouteThread(server, mediaBase, mediaBase);
            fixture.RouteImages(media);
            fixture.RouteThumbs(media);
            string url = General.CleanPageURL("http://user:secret@127.0.0.1:" + server.Port + FourChanThreadFixture.ThreadPath);
            Assert.AreEqual("http://user:secret@127.0.0.1:" + server.Port + FourChanThreadFixture.ThreadPath, url);

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(CreateWatcher(url)));

            Assert.IsNull(server.Requests[0].Header("Authorization"));
            Assert.HasCount(4 + 3, media.Requests);
            string urlWithoutLogin = "http://127.0.0.1:" + server.Port + FourChanThreadFixture.ThreadPath;
            foreach (RecordedRequest request in media.Requests) {
                Assert.AreEqual(urlWithoutLogin, request.Header("Referer"), request.ToString());
                Assert.IsNull(request.Header("Authorization"), request.ToString());
                Assert.DoesNotContain("secret", request.Raw, request.ToString());
            }
        }

        // The names of the request headers other than Connection, sorted
        private static string HeaderNames(RecordedRequest request) {
            return String.Join(",", request.Headers.Keys.Where(k => k != "Connection").OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        }

        private static LoopbackResponse Redirect(int status, string reason, string location) {
            return LoopbackResponse.StatusOnly(status, reason).WithHeader("Location", location);
        }

        private static LoopbackResponse TooManyRequests() {
            LoopbackResponse response = LoopbackResponse.Text("Too Many Requests");
            response.Status = 429;
            response.Reason = "Too Many Requests";
            return response;
        }

        // No response at all until the test ends
        private Func<RecordedRequest, LoopbackResponse> StallBeforeResponse() {
            return r => {
                _release.WaitOne(TimeSpan.FromSeconds(30));
                return LoopbackResponse.Text("late");
            };
        }

        // "ok" if the download returned, otherwise how it failed (see DescribeFailure)
        private static string Outcome(Func<string> download) {
            try {
                download();
                return "ok";
            }
            catch (Exception ex) {
                return DescribeFailure(ex);
            }
        }

        // Runs General.DownloadAsync to its end and returns "ok" or how it failed, and the body
        private static string AsyncOutcome(string url, string auth, out string body) {
            var done = new ManualResetEvent(false);
            var data = new MemoryStream();
            Exception error = null;
            General.DownloadAsync(url, auth, null, false, false, null, r => { }, (b, n) => { lock (data) data.Write(b, 0, n); }, () => done.Set(), ex => { error = ex; done.Set(); });
            Assert.IsTrue(done.WaitOne(TimeSpan.FromSeconds(30)), "Download did not end");
            lock (data) body = Encoding.UTF8.GetString(data.ToArray());
            return error == null ? "ok" : DescribeFailure(error);
        }

        // The only code that knows how the transport reports failures: "timeout", "HTTP <status>"
        // for an HTTP error answer, or "failed" for anything else. MP-5b replaced this mapping (it was
        // HttpWebRequest's WebException), not the tests that use it.
        private static string DescribeFailure(Exception ex) {
            if (ex is TimeoutException && ex.Message.StartsWith("Timed out", StringComparison.Ordinal)) return "timeout";
            HTTPStatusException status = ex as HTTPStatusException;
            if (status != null) return "HTTP " + status.StatusCode;
            return "failed";
        }

        // A file outside the download folder holding a unique marker, which no download may
        // return or save; returns the folder, which the test cleanup deletes
        private string CreateSentinelFolder(params string[] relativePaths) {
            string dir = Path.Combine(Path.GetTempPath(), "ctw-sentinel-" + Guid.NewGuid().ToString("N"));
            _sentinelDirs.Add(dir);
            foreach (string relativePath in relativePaths) {
                string path = Path.Combine(dir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, _sentinel);
            }
            return dir;
        }

        private void AssertSentinelNotSaved() {
            foreach (string path in Directory.GetFiles(DownloadDir, "*", SearchOption.AllDirectories)) {
                Assert.DoesNotContain(_sentinel, File.ReadAllText(path), path);
            }
        }

        private static LoopbackResponse MetaRefresh(string url) {
            return LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head><body>meta refresh page</body></html>");
        }
    }
}
