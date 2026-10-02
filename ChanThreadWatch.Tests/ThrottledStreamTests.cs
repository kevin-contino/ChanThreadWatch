using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // B2
    [TestClass]
    public class ThrottledStreamTests {
        // High enough that Throttle never sleeps, but non-zero so streams count as concurrent downloads.
        private const long FastLimitBytesPerSecond = 1L << 40;

        [ClassInitialize]
        public static void LoadEmptySettings(TestContext context) {
            // Keep the settings store in the test output folder (no settings file there) instead of AppData
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
        }

        [TestInitialize]
        public void EnableThrottling() {
            Settings.MaximumBytesPerSecond = FastLimitBytesPerSecond;
        }

        [TestCleanup]
        public void DisableThrottling() {
            Settings.MaximumBytesPerSecond = null;
        }

        [TestMethod]
        public void ClosingTwiceDoesNotBreakLaterStreams() {
            ThrottledStream first = StartStream();
            first.Close();
            first.Close();

            AssertNextStreamCanRead();
        }

        [TestMethod]
        public void CloseThenDisposeDoesNotBreakLaterStreams() {
            using (ThrottledStream first = StartStream()) {
                first.Close();
            }

            AssertNextStreamCanRead();
        }

        [TestMethod]
        public void ClosingAnUnstartedStreamDoesNotBreakLaterStreams() {
            ThrottledStream unstarted = new ThrottledStream(new MemoryStream(new byte[4]));
            unstarted.Close();
            unstarted.Close();

            AssertNextStreamCanRead();
        }

        [TestMethod]
        public void CloseClosesTheBaseStream() {
            MemoryStream baseStream = new MemoryStream(new byte[4]);
            ThrottledStream stream = new ThrottledStream(baseStream);

            stream.Close();

            Assert.IsFalse(baseStream.CanRead);
        }

        private static ThrottledStream StartStream() {
            ThrottledStream stream = new ThrottledStream(new MemoryStream(new byte[4]), FastLimitBytesPerSecond);
            Assert.AreEqual(4, stream.Read(new byte[4], 0, 4));
            return stream;
        }

        // Each read counts the stream as a concurrent download; a miscounted total of zero
        // makes the weighted speed limit divide by zero.
        private static void AssertNextStreamCanRead() {
            using (ThrottledStream next = new ThrottledStream(new MemoryStream(new byte[4]), FastLimitBytesPerSecond)) {
                Assert.AreEqual(4, next.Read(new byte[4], 0, 4));
            }
        }
    }
}
