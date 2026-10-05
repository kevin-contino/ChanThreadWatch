using System;
using System.IO;
using System.Threading;
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

        [TestMethod]
        public void CloseEndsAThrottleSleepEarly() {
            Settings.MaximumBytesPerSecond = 1;
            ThrottledStream stream = new ThrottledStream(new MemoryStream(new byte[1000]), 1);
            // Let the clock move so the first read is over the limit and sleeps (about 1000 s)
            Thread.Sleep(50);
            var reader = new Thread(() => {
                try { _ = stream.Read(new byte[1000], 0, 1000); }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true };
            reader.Start();
            Thread.Sleep(300);

            stream.Close();

            Assert.IsTrue(reader.Join(TimeSpan.FromSeconds(10)), "The throttled read kept sleeping after Close");
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
