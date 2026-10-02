using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    [TestClass]
    public class ThreadWatcherIntegrationTests : ThreadWatcherIntegrationTestBase {
        private const string PageAuth = "pageuser:pagepass";
        private const string ImageAuth = "imageuser:imagepass";

        [TestMethod]
        public void DownloadsThreadImagesAndThumbnails() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            string threadDir = watcher.ThreadDownloadDirectory;
            Assert.AreEqual(Path.Combine(DownloadDir, "0_wg_100"), threadDir);
            foreach (KeyValuePair<string, byte[]> image in fixture.Images) {
                CollectionAssert.AreEqual(image.Value, File.ReadAllBytes(Path.Combine(threadDir, FourChanThreadFixture.FileName(image.Key))), image.Key);
            }
            foreach (KeyValuePair<string, byte[]> thumb in fixture.Thumbs) {
                CollectionAssert.AreEqual(thumb.Value, File.ReadAllBytes(Path.Combine(threadDir, "thumbs", FourChanThreadFixture.FileName(thumb.Key))), thumb.Key);
            }
            Assert.HasCount(4, Directory.GetFiles(threadDir, "17*"));
            Assert.HasCount(3, Directory.GetFiles(Path.Combine(threadDir, "thumbs")));
            Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.HasCount(1, server.RequestsTo("/image/spoiler-wg.png"));
        }

        [TestMethod]
        public void DownloadsThreadOverKeepAliveConnections() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            server.KeepAlive = true;
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(1 + 4 + 3, server.Requests);
            foreach (KeyValuePair<string, byte[]> image in fixture.Images) {
                CollectionAssert.AreEqual(image.Value, File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, FourChanThreadFixture.FileName(image.Key))), image.Key);
            }
            Assert.IsLessThan(server.Requests.Count, server.ConnectionCount);
        }

        [TestMethod]
        public void SavedPageLinksToLocalFiles() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            string html = File.ReadAllText(SavedPagePath(watcher));
            Assert.DoesNotContain(server.BaseURL(), html);
            Assert.DoesNotContain("{{", html);
            StringAssert.Contains(html, "<a href=\"1700000000001.jpg\" target=\"_blank\">mountain &amp; lake.jpg</a>");
            StringAssert.Contains(html, "<a class=\"fileThumb\" href=\"1700000000001.jpg\"");
            StringAssert.Contains(html, "<img src=\"thumbs/1700000000001s.jpg\"");
            StringAssert.Contains(html, "<a class=\"fileThumb imgspoiler\" href=\"1700000000004.gif\" target=\"_blank\"><img src=\"thumbs/spoiler-wg.png\"");
            Assert.IsFalse(File.Exists(SavedPagePath(watcher) + ".bak"));
        }

        [TestMethod]
        public void MissingThreadStopsWithPageNotFound() {
            LoopbackHttpServer server = StartServer();
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.PageNotFound, reason);
            Assert.HasCount(1, server.Requests);
            Assert.IsFalse(File.Exists(SavedPagePath(watcher)));
        }

        [TestMethod]
        public void SecondPassDoesNotDownloadExistingFilesAgain() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            RunToStop(CreateWatcher(url));

            ThreadWatcher second = CreateWatcher(url);
            StopReason reason = RunToStop(second);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(2, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            foreach (string path in FourChanThreadFixture.ImagePaths) {
                Assert.HasCount(1, server.RequestsTo(path), path);
            }
            foreach (string path in FourChanThreadFixture.ThumbPaths) {
                Assert.HasCount(1, server.RequestsTo(path), path);
            }
            Assert.HasCount(4 + 3 + 2, server.Requests);
            // The re-saved page still points at the files from the first pass
            StringAssert.Contains(File.ReadAllText(SavedPagePath(second)), "<img src=\"thumbs/1700000000001s.jpg\"");
        }

        // Pins current behavior: a hash mismatch is retried once, and if the second try returns the
        // same (wrong) bytes the file is accepted and saved as completed. B21: this tolerance for
        // sites with wrong hashes is kept, but the accepted file is logged as a warning.
        [TestMethod]
        public void HashMismatchWithStableBytesIsRetriedOnceThenAccepted() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string badPath = FourChanThreadFixture.ImagePaths[0];
            byte[] wrongBytes = FourChanThreadFixture.MakeBytes(999, 5000);
            server.Route(badPath, LoopbackResponse.Bytes(wrongBytes, "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(2, server.RequestsTo(badPath));
            string savedPath = Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg");
            CollectionAssert.AreEqual(wrongBytes, File.ReadAllBytes(savedPath));
            StringAssert.Contains(File.ReadAllText(SavedPagePath(watcher)), "<a class=\"fileThumb\" href=\"1700000000001.jpg\"");
            Assert.AreEqual(0, watcher.FailedFileCount);
            StringAssert.Contains(ReadLog(), "Warning: saved " + savedPath + " although it does not match the hash");
        }

        // When every try returns different wrong bytes, the image is tried three times and no file
        // is kept. The run still ends as DownloadComplete, but (B23) the image is counted as
        // failed and logged, and (B22) both links to it in the saved page keep pointing at the
        // image online instead of losing their href.
        [TestMethod]
        public void HashMismatchWithChangingBytesGivesUpAfterThreeTries() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string badPath = FourChanThreadFixture.ImagePaths[0];
            server.RouteSequence(badPath,
                LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(901, 3000), "image/jpeg"),
                LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(902, 3000), "image/jpeg"),
                LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(903, 3000), "image/jpeg"),
                LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(904, 3000), "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(3, server.RequestsTo(badPath));
            Assert.IsFalse(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(1, watcher.FailedFileCount);
            Assert.IsNull(watcher.CheckError);
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(badPath) + ": Download is corrupt");
            string html = File.ReadAllText(SavedPagePath(watcher));
            string remoteURL = server.URL(badPath);
            StringAssert.Contains(html, "<div class=\"fileText\" id=\"fT100\">File: <a href=\"" + remoteURL + "\" target=\"_blank\">mountain &amp; lake.jpg</a>");
            StringAssert.Contains(html, "<a class=\"fileThumb\" href=\"" + remoteURL + "\" target=\"_blank\"><img src=\"thumbs/1700000000001s.jpg\"");
            Assert.DoesNotContain("href=\"1700000000001.jpg\"", html);
        }

        // S2: credentials are only sent to the origin of the thread page
        [TestMethod]
        public void CrossOriginImagesAndThumbnailsGetNoCredentials() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer page = StartServer();
            LoopbackHttpServer media = StartServer();
            string mediaBase = media.BaseURL("localhost");
            fixture.RouteThread(page, mediaBase, mediaBase);
            fixture.RouteImages(media);
            fixture.RouteThumbs(media);
            ThreadWatcher watcher = CreateWatcher(page.URL(FourChanThreadFixture.ThreadPath));
            watcher.PageAuth = PageAuth;
            watcher.ImageAuth = ImageAuth;

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(PageAuth, page.RequestsTo(FourChanThreadFixture.ThreadPath)[0].BasicAuth);
            Assert.HasCount(4 + 3, media.Requests);
            foreach (RecordedRequest request in media.Requests) {
                Assert.IsNull(request.Header("Authorization"), request.ToString());
            }
        }

        // S2, same origin: images carry ImageAuth; thumbnails carry PageAuth (pinned, see
        // ThreadWatcher.StartThumbnailDownload)
        [TestMethod]
        public void SameOriginImagesGetImageAuthAndThumbnailsGetPageAuth() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.PageAuth = PageAuth;
            watcher.ImageAuth = ImageAuth;

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(PageAuth, server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].BasicAuth);
            foreach (string path in FourChanThreadFixture.ImagePaths) {
                Assert.AreEqual(ImageAuth, server.RequestsTo(path)[0].BasicAuth, path);
            }
            foreach (string path in FourChanThreadFixture.ThumbPaths) {
                Assert.AreEqual(PageAuth, server.RequestsTo(path)[0].BasicAuth, path);
            }
        }

        [TestMethod]
        public void ImagesAndThumbnailsSendThePageAsReferer() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);

            RunToStop(CreateWatcher(url));

            Assert.IsNull(server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].Header("Referer"));
            foreach (string path in FourChanThreadFixture.ImagePaths) {
                Assert.AreEqual(url, server.RequestsTo(path)[0].Header("Referer"), path);
            }
            foreach (string path in FourChanThreadFixture.ThumbPaths) {
                Assert.AreEqual(url, server.RequestsTo(path)[0].Header("Referer"), path);
            }
        }
    }
}
