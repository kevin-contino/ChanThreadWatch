using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The Downloads window keeps a finished download listed with its result for the hold time.
    // The main form prunes its progress list; the window shows what is still in that list.
    [TestClass]
    public class DownloadsWindowTests {
        private const long EndTicks = 100000;

        private static DownloadProgressInfo Download(long id, long startTicks, long downloadedSize, long? totalSize = null) {
            return new DownloadProgressInfo {
                DownloadID = id, URL = "http://127.0.0.1/" + id + ".png", TryNumber = 1,
                StartTicks = startTicks, TotalSize = totalSize, DownloadedSize = downloadedSize
            };
        }

        private static DownloadProgressInfo Finished(long id, bool isSuccessful) {
            DownloadProgressInfo info = Download(id, EndTicks - 500, 2048);
            info.EndTicks = EndTicks;
            info.TotalSize = info.DownloadedSize;
            info.IsSuccessful = isSuccessful;
            return info;
        }

        private static Dictionary<long, DownloadProgressInfo> ListOf(params DownloadProgressInfo[] infos) {
            var downloadProgresses = new Dictionary<long, DownloadProgressInfo>();
            foreach (DownloadProgressInfo info in infos) downloadProgresses[info.DownloadID] = info;
            return downloadProgresses;
        }

        // The 5000 and 5001 rows below pin the 5 second hold time
        [TestMethod]
        [DataRow(0)]
        [DataRow(1000)]
        [DataRow(5000)]
        public void FinishedDownloadIsKeptWithinTheHoldTime(int msSinceEnd) {
            Dictionary<long, DownloadProgressInfo> downloadProgresses = ListOf(Finished(1, true));
            frmChanThreadWatch.RemoveExpiredDownloadProgresses(downloadProgresses, EndTicks + msSinceEnd);
            Assert.IsTrue(downloadProgresses.ContainsKey(1));
        }

        [TestMethod]
        [DataRow(5001)]
        [DataRow(60000)]
        public void FinishedDownloadIsRemovedAfterTheHoldTime(int msSinceEnd) {
            Dictionary<long, DownloadProgressInfo> downloadProgresses = ListOf(Finished(1, true), Finished(2, false));
            frmChanThreadWatch.RemoveExpiredDownloadProgresses(downloadProgresses, EndTicks + msSinceEnd);
            Assert.IsEmpty(downloadProgresses);
        }

        [TestMethod]
        public void DownloadInProgressIsNeverRemoved() {
            Dictionary<long, DownloadProgressInfo> downloadProgresses = ListOf(Download(1, 0, 10), Finished(2, true));
            frmChanThreadWatch.RemoveExpiredDownloadProgresses(downloadProgresses, EndTicks + 600000);
            CollectionAssert.AreEquivalent(new long[] { 1 }, new List<long>(downloadProgresses.Keys));
        }

        [TestMethod]
        [DataRow(true, "Done")]
        [DataRow(false, "Failed")]
        public void FinishedDownloadShowsItsResult(bool isSuccessful, string expected) {
            Assert.AreEqual(expected, frmDownloads.GetProgressText(Finished(1, isSuccessful)));
        }

        [TestMethod]
        public void DownloadInProgressShowsPercentDone() {
            Assert.AreEqual("25%", frmDownloads.GetProgressText(Download(1, 0, 500, 2000)));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow(0L)]
        public void DownloadInProgressWithoutAKnownSizeShowsNoPercent(long? totalSize) {
            Assert.AreEqual(string.Empty, frmDownloads.GetProgressText(Download(1, 0, 500, totalSize)));
        }

        // The title counts the bytes of every listed download, finished ones included, over the
        // last 2 seconds: 100 KB + 50 KB in the 1 second since the first download started.
        [TestMethod]
        public void TitleShowsTotalSpeedIncludingFinishedDownloads() {
            const long ticksNow = 10000;
            DownloadProgressInfo running = Download(1, 9000, 102400, 204800);
            DownloadProgressInfo finished = Download(2, 9500, 51200);
            finished.EndTicks = 9800;
            finished.TotalSize = finished.DownloadedSize;
            finished.IsSuccessful = true;
            var bytesPerSecByID = new Dictionary<long, long?>();

            string title = frmDownloads.TakeSnapshots(new List<DownloadProgressInfo> { running, finished },
                new Dictionary<long, List<DownloadedSizeSnapshot>>(), ticksNow, bytesPerSecByID);

            Assert.AreEqual("Downloads - 150 KB/s", title);
            Assert.AreEqual(102400L, bytesPerSecByID[1]);
            Assert.AreEqual(102400L, bytesPerSecByID[2]);
        }

        [TestMethod]
        public void TitleHasNoSpeedWithoutDownloads() {
            string title = frmDownloads.TakeSnapshots(new List<DownloadProgressInfo>(),
                new Dictionary<long, List<DownloadedSizeSnapshot>>(), 10000, new Dictionary<long, long?>());
            Assert.AreEqual("Downloads", title);
        }

        [TestMethod]
        public void SnapshotsOfDownloadsNoLongerListedAreDropped() {
            var snapshotLists = new Dictionary<long, List<DownloadedSizeSnapshot>>();
            frmDownloads.TakeSnapshots(new List<DownloadProgressInfo> { Download(1, 9000, 100), Download(2, 9000, 100) },
                snapshotLists, 10000, new Dictionary<long, long?>());
            frmDownloads.TakeSnapshots(new List<DownloadProgressInfo> { Download(2, 9000, 200) },
                snapshotLists, 11000, new Dictionary<long, long?>());
            CollectionAssert.AreEquivalent(new long[] { 2 }, new List<long>(snapshotLists.Keys));
        }

        [TestMethod]
        public void StartCopiesTheEventFields() {
            DownloadProgressInfo info = frmChanThreadWatch.StartDownloadProgress(new DownloadStartEventArgs(7, "http://127.0.0.1/7.png", 2, 4096), 1234);
            Assert.AreEqual(7L, info.DownloadID);
            Assert.AreEqual("http://127.0.0.1/7.png", info.URL);
            Assert.AreEqual(2, info.TryNumber);
            Assert.AreEqual(1234L, info.StartTicks);
            Assert.AreEqual(4096L, info.TotalSize);
            Assert.IsNull(info.EndTicks);
        }

        private static DownloadProgressInfo Started(long? announcedSize) {
            return frmChanThreadWatch.StartDownloadProgress(new DownloadStartEventArgs(1, "http://127.0.0.1/1.png", 1, announcedSize), 1000);
        }

        [TestMethod]
        public void SuccessfulEndSetsTheFinalSize() {
            DownloadProgressInfo info = frmChanThreadWatch.EndDownloadProgress(Started(4096), new DownloadEndEventArgs(1, 4000, true), 2000);
            Assert.AreEqual(2000L, info.EndTicks);
            Assert.AreEqual(4000L, info.DownloadedSize);
            Assert.AreEqual(4000L, info.TotalSize);
            Assert.IsTrue(info.IsSuccessful);
        }

        [TestMethod]
        [DataRow(4096L)]
        [DataRow(null)]
        public void FailedEndKeepsTheAnnouncedSize(long? announcedSize) {
            DownloadProgressInfo info = frmChanThreadWatch.EndDownloadProgress(Started(announcedSize), new DownloadEndEventArgs(1, 1000, false), 2000);
            Assert.AreEqual(2000L, info.EndTicks);
            Assert.AreEqual(1000L, info.DownloadedSize);
            Assert.AreEqual(announcedSize, info.TotalSize);
            Assert.IsFalse(info.IsSuccessful);
        }

        [TestMethod]
        public void FirstEndWins() {
            DownloadProgressInfo done = frmChanThreadWatch.EndDownloadProgress(Started(4096), new DownloadEndEventArgs(1, 4096, true), 2000);
            DownloadProgressInfo info = frmChanThreadWatch.EndDownloadProgress(done, new DownloadEndEventArgs(1, 10, false), 3000);
            Assert.AreEqual(2000L, info.EndTicks);
            Assert.AreEqual(4096L, info.DownloadedSize);
            Assert.AreEqual(4096L, info.TotalSize);
            Assert.IsTrue(info.IsSuccessful);
        }

        [TestMethod]
        public void SizeColumnShowsAnnouncedOrDownloadedSize() {
            Assert.AreEqual("200 KB", frmDownloads.GetSizeText(Download(1, 0, 10240, 204800)));
            Assert.AreEqual("10 KB", frmDownloads.GetSizeText(Download(1, 0, 10240)));
            DownloadProgressInfo failed = frmChanThreadWatch.EndDownloadProgress(Started(204800), new DownloadEndEventArgs(1, 10240, false), 2000);
            Assert.AreEqual("200 KB", frmDownloads.GetSizeText(failed));
            DownloadProgressInfo done = frmChanThreadWatch.EndDownloadProgress(Started(null), new DownloadEndEventArgs(1, 30720, true), 2000);
            Assert.AreEqual("30 KB", frmDownloads.GetSizeText(done));
        }

        [TestMethod]
        public void SpeedColumnIsBlankOnceTheDownloadEnds() {
            Assert.AreEqual("2 KB/s", frmDownloads.GetSpeedText(Download(1, 0, 10240), 2048));
            Assert.AreEqual(string.Empty, frmDownloads.GetSpeedText(Finished(1, true), 2048));
            Assert.AreEqual(string.Empty, frmDownloads.GetSpeedText(Finished(1, false), 2048));
        }
    }
}
