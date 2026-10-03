using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Parsing Retry-After, choosing the length of a host's rate limit pause, and showing it
    [TestClass]
    public class RateLimitTests {
        private static readonly DateTime Now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        [TestCleanup]
        public void RestorePauseLimits() {
            ConnectionManager.MinRateLimitPauseMS = ConnectionManager.DefaultMinRateLimitPauseMS;
            ConnectionManager.MaxRateLimitPauseMS = ConnectionManager.DefaultMaxRateLimitPauseMS;
            ConnectionManager.UnspecifiedRateLimitPauseMS = ConnectionManager.DefaultUnspecifiedRateLimitPauseMS;
            ConnectionManager.ResetForTesting();
        }

        [TestMethod]
        [DataRow("2140", 2140)]
        [DataRow(" 2947 ", 2947)]
        [DataRow("0", 0)]
        [DataRow("Sat, 03 Oct 2026 12:00:30 GMT", 30)]
        [DataRow("Saturday, 03-Oct-26 12:01:00 GMT", 60)]
        [DataRow("Sat Oct 3 12:00:10 2026", 10)]
        [DataRow("Sat, 03 Oct 2026 11:00:00 GMT", 0)]
        [DataRow("99999999999999999999", Int32.MaxValue)]
        public void RetryAfterIsParsedAsSecondsOrHttpDate(string value, double expectedSeconds) {
            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), General.ParseRetryAfter(value, Now));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("soon")]
        [DataRow("-5")]
        [DataRow("1.5")]
        public void InvalidRetryAfterGivesNoWait(string value) {
            Assert.IsNull(General.ParseRetryAfter(value, Now));
        }

        // The production limits: at most 2 hours, at least 5 seconds, whatever the server says
        [TestMethod]
        public void PauseIsCappedAndFloored() {
            Assert.AreEqual(2 * 60 * 60 * 1000, Manager("cap").Pause(TimeSpan.FromSeconds(Int32.MaxValue)));
            Assert.AreEqual(5 * 1000, Manager("floor").Pause(TimeSpan.Zero));
            Assert.AreEqual(2140 * 1000, Manager("given").Pause(TimeSpan.FromSeconds(2140)));
        }

        // Without a wait time the pause doubles each time, but not for answers that arrive during
        // a pause, and starts over once a download succeeds
        [TestMethod]
        public void PauseWithoutWaitTimeBacksOffExponentially() {
            ConnectionManager.MinRateLimitPauseMS = 1;
            ConnectionManager.UnspecifiedRateLimitPauseMS = 100;
            ConnectionManager manager = Manager("backoff");

            Assert.AreEqual(100, manager.Pause(null));
            manager.Pause(null);
            manager.Pause(null);
            WaitForPauseToEnd(manager);
            Assert.AreEqual(200, manager.Pause(null));
            WaitForPauseToEnd(manager);
            Assert.AreEqual(400, manager.Pause(null));
            manager.ResetRateLimitBackoff();
            WaitForPauseToEnd(manager);
            Assert.AreEqual(100, manager.Pause(null));
        }

        // A later, shorter wait time does not cut a pause short
        [TestMethod]
        public void PauseIsOnlyExtended() {
            ConnectionManager manager = Manager("extend");
            manager.Pause(TimeSpan.FromSeconds(60));

            Assert.IsGreaterThan(50 * 1000, manager.Pause(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(manager.IsPaused);
        }

        // A waiter that gives up keeps no count and is skipped by the next release
        [TestMethod]
        public void SemaphoreWaitCanBeCanceled() {
            var semaphore = new FIFOSemaphore(0, 1);

            var wait = System.Threading.Tasks.Task.Run(() => semaphore.WaitOne(10, () => true));

            Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(5)), "The canceled wait did not return");
            Assert.IsFalse(wait.Result);
            semaphore.Release();
            Assert.IsTrue(semaphore.WaitOne(0));
        }

        // Resetting forgets the hosts, so a connection slot a test leaked is free again
        [TestMethod]
        public void ResetForTestingFreesConnectionSlots() {
            Assert.IsNotNull(Manager("slots").ObtainConnectionGroupName(() => true));

            ConnectionManager.ResetForTesting();

            Assert.IsNotNull(Manager("slots").ObtainConnectionGroupName(() => true));
        }

        [TestMethod]
        public void WaitStatusShowsTheRateLimit() {
            CultureInfo culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try {
                DateTime resume = new DateTime(2026, 10, 3, 14, 32, 5);

                Assert.AreEqual("Rate limited by i.4cdn.org until 14:32:05",
                    frmChanThreadWatch.FormatWaitStatus(60, null, 0, "i.4cdn.org", resume));
                Assert.AreEqual("Error: HTTP 403 Forbidden, waiting 60 seconds",
                    frmChanThreadWatch.FormatWaitStatus(60, "HTTP 403 Forbidden", 0, "i.4cdn.org", resume));
            }
            finally {
                Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        private static ConnectionManager Manager(string name) {
            return ConnectionManager.GetInstance("http://" + name + ".ratelimit.invalid/");
        }

        private static void WaitForPauseToEnd(ConnectionManager manager) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (manager.IsPaused) {
                Assert.IsTrue(DateTime.UtcNow < deadline, "The pause did not end");
                Thread.Sleep(10);
            }
        }
    }
}
