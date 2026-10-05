using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // How requests to one host are paced: one download at a time per host, across all watchers,
    // and a minimum interval between the starts of consecutive requests to the host
    [TestClass]
    public class ConnectionPacingTests : ThreadWatcherIntegrationTestBase {
        private const string MediaHost = "localhost";

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<KeyValuePair<string, long>> _arrivals = new List<KeyValuePair<string, long>>();
        private int _inFlight;
        private int _maxInFlight;

        // Two watchers download the same thread at once, yet the host never serves more than one
        // request at a time
        [TestMethod]
        public void OnlyOneRequestIsInFlightPerHost() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteFiles(server, fixture, TimeSpan.FromMilliseconds(50));
            RouteRecordedThread(server, fixture, server.BaseURL(), server.BaseURL());
            ThreadWatcher first = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            ThreadWatcher second = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            second.ThreadDownloadDirectory = Path.Combine(DownloadDir, "second");

            RunBothToStop(first, second);

            Assert.AreEqual(2 * (1 + 4 + 3), CountArrivals());
            Assert.AreEqual(1, Volatile.Read(ref _maxInFlight));
            Assert.HasCount(4, Directory.GetFiles(second.ThreadDownloadDirectory, "1700000000*.*"));
        }

        // Consecutive requests to one host, retries included, start at least the interval apart. The
        // server sees arrivals, not starts, so the bound leaves 150 ms for a slower connect (a retry
        // opens a new connection) on a loaded runner; unpaced requests arrive milliseconds apart.
        [TestMethod]
        public void ConsecutiveRequestStartsToOneHostAreSpacedOut() {
            ConnectionManager.MinRequestStartIntervalMS = 400;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteFiles(server, fixture, TimeSpan.Zero);
            LoopbackResponse error = LoopbackResponse.StatusOnly(500, "Internal Server Error");
            string firstImage = FourChanThreadFixture.ImagePaths[0];
            Route(server, firstImage, error, fixture.Images[firstImage]);
            RouteRecordedThread(server, fixture, server.BaseURL(), server.BaseURL());
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            Assert.AreEqual(StopReason.DownloadComplete, RunToStop(watcher));

            List<long> times = ArrivalTimes(null);
            Assert.HasCount(1 + 5 + 3, times);
            for (int i = 1; i < times.Count; i++) {
                Assert.IsGreaterThanOrEqualTo(250, times[i] - times[i - 1], "request " + i);
            }
        }

        // The interval is per host: the first image, on another host, does not wait for the page.
        // The bound leaves a slow runner seconds to parse the page, and a shared interval
        // would hold the first image back twice as long as the bound.
        [TestMethod]
        public void DifferentHostsAreNotDelayedByEachOther() {
            ConnectionManager.MinRequestStartIntervalMS = 10000;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            RouteFiles(server, fixture, TimeSpan.Zero);
            RouteRecordedThread(server, fixture, server.BaseURL(MediaHost), server.BaseURL(MediaHost));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var started = new ManualResetEvent(false);
            watcher.DownloadStart += (s, e) => { if (e.URL.Contains(MediaHost)) started.Set(); };
            watcher.Start();

            Assert.IsTrue(started.WaitOne(RunTimeout), "No file download started");
            long pageTime = ArrivalTimes(FourChanThreadFixture.ThreadPath)[0];
            long firstFileTime = ArrivalTimes(FourChanThreadFixture.ImagePaths[0])[0];
            Assert.IsLessThan(5000, firstFileTime - pageTime);
            watcher.Stop(StopReason.UserRequest);
            Assert.IsTrue(watcher.WaitUntilStopped((int)RunTimeout.TotalMilliseconds), "Check did not finish");
        }

        private void RouteFiles(LoopbackHttpServer server, FourChanThreadFixture fixture, TimeSpan serveTime) {
            foreach (KeyValuePair<string, byte[]> file in fixture.Images) Route(server, file.Key, null, file.Value, serveTime);
            foreach (KeyValuePair<string, byte[]> file in fixture.Thumbs) Route(server, file.Key, null, file.Value, serveTime);
        }

        private void RouteRecordedThread(LoopbackHttpServer server, FourChanThreadFixture fixture, string imageBaseURL, string thumbBaseURL) {
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(imageBaseURL, thumbBaseURL));
            server.Route(FourChanThreadFixture.ThreadPath, r => Record(r, () => page, TimeSpan.FromMilliseconds(50)));
        }

        // Serves the file, or firstResponse to the first request if it is given
        private void Route(LoopbackHttpServer server, string path, LoopbackResponse firstResponse, byte[] data, TimeSpan serveTime = default(TimeSpan)) {
            int count = 0;
            server.Route(path, r => Record(r, () => {
                bool first = Interlocked.Increment(ref count) == 1;
                return first && firstResponse != null ? firstResponse : LoopbackResponse.Bytes(data, "image/jpeg");
            }, serveTime));
        }

        // Records when the request arrived and how many were being served at the same time
        private LoopbackResponse Record(RecordedRequest request, Func<LoopbackResponse> respond, TimeSpan serveTime) {
            lock (_arrivals) _arrivals.Add(new KeyValuePair<string, long>(request.Path, _clock.ElapsedMilliseconds));
            int inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, inFlight);
            try {
                if (serveTime > TimeSpan.Zero) Thread.Sleep(serveTime);
                return respond();
            }
            finally {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value) {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }

        private int CountArrivals() {
            lock (_arrivals) return _arrivals.Count;
        }

        // Arrival times of the requests to the path (all requests if null), in order
        private List<long> ArrivalTimes(string path) {
            var times = new List<long>();
            lock (_arrivals) {
                foreach (KeyValuePair<string, long> arrival in _arrivals) {
                    if (path == null || arrival.Key == path) times.Add(arrival.Value);
                }
            }
            return times;
        }

        private static void RunBothToStop(ThreadWatcher first, ThreadWatcher second) {
            var stopped = new CountdownEvent(2);
            first.StopStatus += (s, e) => stopped.Signal();
            second.StopStatus += (s, e) => stopped.Signal();
            first.Start();
            second.Start();
            Assert.IsTrue(stopped.Wait(RunTimeout), "The watchers did not stop within " + RunTimeout);
            Assert.IsTrue(first.WaitUntilStopped((int)RunTimeout.TotalMilliseconds));
            Assert.IsTrue(second.WaitUntilStopped((int)RunTimeout.TotalMilliseconds));
        }
    }
}
