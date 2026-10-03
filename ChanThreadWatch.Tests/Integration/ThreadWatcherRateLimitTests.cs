using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // How the watcher reacts when a server rate limits it (HTTP 429, or 503 with Retry-After): it
    // pauses every request to that host, from every watcher, and retries after the pause
    [TestClass]
    public class ThreadWatcherRateLimitTests : ThreadWatcherIntegrationTestBase {
        private const string MediaHost = "localhost";
        private static readonly string FirstImage = FourChanThreadFixture.ImagePaths[0];
        private static readonly TimeSpan PauseEndTimeout = TimeSpan.FromSeconds(10);

        [TestInitialize]
        public void UseShortPauses() {
            ConnectionManager.MinRateLimitPauseMS = 100;
        }

        // A file that gets 429 with Retry-After (in seconds) is not counted as failed, the thread
        // shows the rate limit, and the file is retried and linked in the first check after the
        // pause, even though the page has not changed
        [TestMethod]
        public void FileRateLimitedWithRetryAfterSecondsIsRetriedAfterThePause() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.RouteSequence(FirstImage, TooManyRequests().WithHeader("Retry-After", "1"),
                LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var failedCounts = new List<int>();
            var rateLimitedHosts = new List<string>();
            var statuses = new List<string>();
            watcher.WaitStatus += (s, e) => {
                failedCounts.Add(watcher.FailedFileCount);
                rateLimitedHosts.Add(watcher.RateLimitedHost);
                statuses.Add(frmChanThreadWatch.FormatWaitStatus(60, watcher.CheckError, watcher.FailedFileCount, watcher.RateLimitPausedHost, watcher.RateLimitResumeTime));
            };

            RunChecks(watcher, 2, check => {
                WaitForPauseToEnd(server.URL(FirstImage, MediaHost));
                // Once the pause is over, the status no longer shows it, though the file waits for the next check
                Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
                Assert.IsNull(watcher.RateLimitPausedHost);
            });

            CollectionAssert.AreEqual(new[] { 0, 0 }, failedCounts);
            CollectionAssert.AreEqual(new[] { MediaHost, null }, rateLimitedHosts);
            StringAssert.Matches(statuses[0], new System.Text.RegularExpressions.Regex(@"^Rate limited by localhost until \d\d:\d\d:\d\d$"));
            Assert.AreEqual("Waiting 60 seconds", statuses[1]);
            Assert.HasCount(2, server.RequestsTo(FirstImage));
            Assert.IsNull(server.RequestsTo(FourChanThreadFixture.ThreadPath)[1].Header("If-Modified-Since"));
            CollectionAssert.AreEqual(fixture.Images[FirstImage], File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            StringAssert.Contains(File.ReadAllText(SavedPagePath(watcher)), "<a class=\"fileThumb\" href=\"1700000000001.jpg\"");
            StringAssert.Contains(ReadLog(), "Rate limited by localhost, pausing requests to it for 1 seconds.");
        }

        // Retry-After may be an HTTP date instead of a number of seconds
        [TestMethod]
        public void RetryAfterAsHttpDateSetsThePause() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.Route(FirstImage, TooManyRequests().WithHeader("Retry-After", DateTime.UtcNow.AddSeconds(6).ToString("r")));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            AssertPausedFor(watcher, server, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(7));
            Assert.HasCount(1, server.RequestsTo(FirstImage));
        }

        // Without Retry-After, the host is paused for the default backoff
        [TestMethod]
        public void MissingRetryAfterUsesTheDefaultBackoff() {
            ConnectionManager.UnspecifiedRateLimitPauseMS = 3000;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.Route(FirstImage, TooManyRequests());
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            AssertPausedFor(watcher, server, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3.5));
            StringAssert.Contains(ReadLog(), "Rate limited by localhost, pausing requests to it for 3 seconds (no Retry-After given).");
        }

        // 503 with Retry-After is a rate limit too: not retried at once and not counted as failed
        // (503 without Retry-After stays an ordinary failure, see ImageThatFailsEveryTryIsCountedAndLogged)
        [TestMethod]
        public void ServiceUnavailableWithRetryAfterIsARateLimit() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.Route(FirstImage, LoopbackResponse.StatusOnly(503, "Service Unavailable").WithHeader("Retry-After", "30"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
            Assert.AreEqual(0, watcher.FailedFileCount);
            Assert.HasCount(1, server.RequestsTo(FirstImage));
        }

        // A Retry-After far in the future is capped
        [TestMethod]
        public void HugeRetryAfterIsCapped() {
            ConnectionManager.MaxRateLimitPauseMS = 3000;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.Route(FirstImage, TooManyRequests().WithHeader("Retry-After", "999999999"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            AssertPausedFor(watcher, server, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3.5));
        }

        // The pause is shared: another watcher sends nothing to the paused host, while its page
        // and thumbnails, on another host, still download
        [TestMethod]
        public void SecondWatcherSendsNothingToThePausedHost() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteThreadWithImagesOnMediaHost(fixture, server);
            server.Route(FirstImage, TooManyRequests().WithHeader("Retry-After", "30"));
            ThreadWatcher first = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            RunChecks(first, 1);
            int imageRequests = CountImageRequests(server);
            int pageRequests = server.RequestsTo(FourChanThreadFixture.ThreadPath).Count;
            ThreadWatcher second = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            second.ThreadDownloadDirectory = Path.Combine(DownloadDir, "second");

            RunChecks(second, 1);

            Assert.AreEqual(imageRequests, CountImageRequests(server));
            Assert.HasCount(pageRequests + 1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.AreEqual(MediaHost, second.RateLimitedHost);
            Assert.AreEqual(0, second.FailedFileCount);
            Assert.IsNull(second.CheckError);
            Assert.IsTrue(File.Exists(Path.Combine(second.ThreadDownloadDirectory, "thumbs", "1700000000001s.jpg")));
            Assert.IsEmpty(Directory.GetFiles(second.ThreadDownloadDirectory, "1700000000*.*"));
        }

        // A rate limited thread page is not reported as an error or retried at once, and a
        // one-time download keeps going until it has been retried after the pause
        [TestMethod]
        public void RateLimitedPageKeepsOneTimeDownloadGoingUntilRetried() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.RouteSequence(FourChanThreadFixture.ThreadPath, TooManyRequests().WithHeader("Retry-After", "1"),
                LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL())));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var waiting = new ManualResetEvent(false);
            var stopped = new ManualResetEvent(false);
            StopReason reason = StopReason.Other;
            watcher.WaitStatus += (s, e) => waiting.Set();
            watcher.StopStatus += (s, e) => { reason = e.StopReason; stopped.Set(); };

            watcher.Start();
            Assert.IsTrue(waiting.WaitOne(RunTimeout), "The one-time download did not keep watching");
            Assert.AreEqual(PageHost, watcher.RateLimitedHost);
            Assert.IsNull(watcher.CheckError);
            Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            WaitForPauseToEnd(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.MillisecondsUntilNextCheck = 0;

            Assert.IsTrue(stopped.WaitOne(RunTimeout), "ThreadWatcher did not stop within " + RunTimeout);
            Assert.IsTrue(watcher.WaitUntilStopped((int)RunTimeout.TotalMilliseconds), "Check did not finish");
            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.IsNull(watcher.RateLimitedHost);
            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
        }

        // A page that keeps loading does not reset the backoff of a host whose file keeps getting
        // 429 without Retry-After, so the pause grows from check to check
        [TestMethod]
        public void BackoffGrowsWhileOnlyThePageLoads() {
            ConnectionManager.UnspecifiedRateLimitPauseMS = 400;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, TooManyRequests());
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var pauses = new List<long>();
            watcher.WaitStatus += (s, e) => pauses.Add(ConnectionManager.GetInstance(server.URL(FirstImage)).PausedUntilTicks - TickCount.Now);

            RunChecks(watcher, 3, check => WaitForPauseToEnd(server.URL(FirstImage)));

            Assert.HasCount(3, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.HasCount(3, server.RequestsTo(FirstImage));
            Assert.IsLessThanOrEqualTo(400, pauses[0]);
            Assert.IsGreaterThan(500, pauses[1], "second pause");
            Assert.IsGreaterThan(1000, pauses[2], "third pause");
        }

        // A watcher waiting for the host's only connection, held by another watcher, stops
        // promptly instead of waiting for the connection
        [TestMethod]
        public void WatcherWaitingForAConnectionStopsPromptly() {
            LoopbackHttpServer server = StartServer();
            var release = new ManualResetEvent(false);
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.RawThenStall("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n<html>", release));
            ThreadWatcher holder = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            ThreadWatcher waiter = CreateWatcher(server.URL("/wg/thread/101"));
            waiter.ThreadDownloadDirectory = Path.Combine(DownloadDir, "waiter");
            var stopped = new ManualResetEvent(false);
            waiter.StopStatus += (s, e) => stopped.Set();
            try {
                holder.Start();
                WaitUntil(() => server.RequestsTo(FourChanThreadFixture.ThreadPath).Count == 1);
                waiter.Start();
                Thread.Sleep(500);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                waiter.Stop(StopReason.UserRequest);

                Assert.IsTrue(stopped.WaitOne(TimeSpan.FromSeconds(3)), "The waiting watcher did not stop promptly");
                Assert.IsLessThan(2000, clock.ElapsedMilliseconds);
                Assert.IsEmpty(server.RequestsTo("/wg/thread/101"));
            }
            finally {
                release.Set();
                holder.Stop(StopReason.UserRequest);
                holder.WaitUntilStopped((int)RunTimeout.TotalMilliseconds);
                waiter.WaitUntilStopped((int)RunTimeout.TotalMilliseconds);
            }
        }

        // An unexpected failure when a delayed request starts (on a scheduler thread) is reported
        // and still ends the download, so its connection is released and the other files load
        [TestMethod]
        public void FailureStartingADelayedRequestStillEndsTheDownload() {
            ConnectionManager.MinRequestStartIntervalMS = 50;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string failingURL = server.URL(FirstImage);
            string marker = "injected start failure " + Guid.NewGuid().ToString("N");
            ThreadWatcher.BeforeRequestStart = url => {
                if (url == failingURL) throw new InvalidOperationException(marker);
            };
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(1, watcher.FailedFileCount);
            Assert.IsEmpty(server.RequestsTo(FirstImage));
            Assert.HasCount(3, Directory.GetFiles(watcher.ThreadDownloadDirectory, "1700000000*.*"));
            StringAssert.Contains(ReadLog(), marker);
        }

        // A synchronous page download (used for 4chan thread names) sends nothing to a paused
        // host, and a 429 to it pauses the host
        [TestMethod]
        public void DownloadPageToStringHonorsAndSetsThePause() {
            LoopbackHttpServer server = StartServer();
            server.Route("/page", TooManyRequests().WithHeader("Retry-After", "30"));
            string url = server.URL("/page", MediaHost);

            Assert.ThrowsExactly<HTTPRateLimitedException>(() => General.DownloadPageToString(url));
            Assert.IsTrue(ConnectionManager.GetInstance(url).IsPaused);
            Assert.ThrowsExactly<RateLimitException>(() => General.DownloadPageToString(url));
            Assert.HasCount(1, server.RequestsTo("/page"));
        }

        // A meta refresh to a paused host is not followed
        [TestMethod]
        public void MetaRefreshToAPausedHostIsNotFollowed() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, MetaRefresh(server.URL("/target", MediaHost)));
            server.Route("/target", LoopbackResponse.Html("<html></html>"));
            ConnectionManager.GetInstanceForHost(MediaHost).Pause(TimeSpan.FromSeconds(30));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            Assert.IsEmpty(server.RequestsTo("/target"));
            Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
            Assert.IsNull(watcher.CheckError);
        }

        // A 429 from a meta refresh target pauses that host, not the host of the page
        [TestMethod]
        public void RateLimitFromAMetaRefreshTargetPausesThatHost() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, MetaRefresh(server.URL("/target", MediaHost)));
            server.Route("/target", TooManyRequests().WithHeader("Retry-After", "30"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 1);

            Assert.IsTrue(ConnectionManager.GetInstanceForHost(MediaHost).IsPaused);
            Assert.IsFalse(ConnectionManager.GetInstanceForHost(PageHost).IsPaused);
            Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
            Assert.HasCount(1, server.RequestsTo("/target"));
        }

        // An exception in a background work item is logged instead of ending the process
        [TestMethod]
        public void ExceptionInABackgroundWorkItemIsLogged() {
            var ran = new ManualResetEvent(false);
            string marker = "injected work item failure " + Guid.NewGuid().ToString("N");
            ThreadPoolManager.QueueWorkItem("rate limit test", () => { throw new InvalidOperationException(marker); });
            ThreadPoolManager.QueueWorkItem("rate limit test", () => ran.Set());

            Assert.IsTrue(ran.WaitOne(RunTimeout));
            WaitUntil(() => ReadLog().Contains(marker));
        }

        private static LoopbackResponse MetaRefresh(string url) {
            return LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=" + url + "\"></head></html>");
        }

        private static void WaitUntil(Func<bool> condition) {
            DateTime deadline = DateTime.UtcNow + PauseEndTimeout;
            while (!condition()) {
                Assert.IsTrue(DateTime.UtcNow < deadline, "Condition not met within " + PauseEndTimeout);
                Thread.Sleep(20);
            }
        }

        private static LoopbackResponse TooManyRequests() {
            LoopbackResponse response = LoopbackResponse.Text("Too Many Requests");
            response.Status = 429;
            response.Reason = "Too Many Requests";
            return response;
        }

        // Thread page (answering If-Modified-Since with 304) and thumbnails on 127.0.0.1, images
        // on localhost: the same server under two host names
        private static void RouteThreadWithImagesOnMediaHost(FourChanThreadFixture fixture, LoopbackHttpServer server) {
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(MediaHost), server.BaseURL()))
                .WithHeader("Last-Modified", "Wed, 01 Jan 2025 00:00:00 GMT");
            server.Route(FourChanThreadFixture.ThreadPath, r => r.Header("If-Modified-Since") != null ? LoopbackResponse.StatusOnly(304, "Not Modified") : page);
            fixture.RouteImages(server);
            fixture.RouteThumbs(server);
        }

        private static int CountImageRequests(LoopbackHttpServer server) {
            int count = 0;
            foreach (string path in FourChanThreadFixture.ImagePaths) {
                count += server.RequestsTo(path).Count;
            }
            return count;
        }

        private static void AssertPausedFor(ThreadWatcher watcher, LoopbackHttpServer server, TimeSpan atLeast, TimeSpan atMost) {
            Assert.AreEqual(MediaHost, watcher.RateLimitedHost);
            Assert.IsTrue(ConnectionManager.GetInstance(server.URL(FirstImage, MediaHost)).IsPaused);
            TimeSpan remaining = watcher.RateLimitResumeTime - DateTime.Now;
            Assert.IsTrue(remaining >= atLeast && remaining <= atMost, "Pause ends in " + remaining);
        }

        private static void WaitForPauseToEnd(string url) {
            ConnectionManager manager = ConnectionManager.GetInstance(url);
            DateTime deadline = DateTime.UtcNow + PauseEndTimeout;
            while (manager.IsPaused) {
                Assert.IsTrue(DateTime.UtcNow < deadline, "The pause did not end within " + PauseEndTimeout);
                Thread.Sleep(50);
            }
        }
    }
}
