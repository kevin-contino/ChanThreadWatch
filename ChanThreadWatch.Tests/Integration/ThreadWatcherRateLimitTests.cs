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
                statuses.Add(frmChanThreadWatch.FormatWaitStatus(60, watcher.CheckError, watcher.FailedFileCount, watcher.RateLimitedHost, watcher.RateLimitResumeTime));
            };

            RunChecks(watcher, 2, check => WaitForPauseToEnd(server.URL(FirstImage, MediaHost)));

            CollectionAssert.AreEqual(new[] { 0, 0 }, failedCounts);
            CollectionAssert.AreEqual(new[] { MediaHost, null }, rateLimitedHosts);
            StringAssert.StartsWith(statuses[0], "Rate limited by localhost, resuming at ");
            StringAssert.EndsWith(statuses[0], ", waiting 60 seconds");
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
