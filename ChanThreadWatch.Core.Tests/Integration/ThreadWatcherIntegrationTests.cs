using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
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
            Assert.AreEqual(Path.Combine(DownloadDir, "127.0.0.1_wg_100"), threadDir);
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
            // MP-2c (W2): .NET 10's HttpWebRequest opens one connection per request (no keep-alive reuse) until the
            // HttpClient transport (MP-5b). On .NET Framework there were fewer connections than requests:
            // Assert.IsLessThan(server.Requests.Count, server.ConnectionCount);
            Assert.AreEqual(server.Requests.Count, server.ConnectionCount);
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

        // A browser decodes the saved file by its byte order mark, so it reads the text that the
        // removal and SavedPageSweep checked, whatever charset the page declares
        [TestMethod]
        public void SavedPageIsUTF8WithAByteOrderMark() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            byte[] bytes = File.ReadAllBytes(SavedPagePath(watcher));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, new[] { bytes[0], bytes[1], bytes[2] });
        }

        // S5
        [TestMethod]
        public void SavedPageHasNoActiveContent() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            string html = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(html, "<head>" + OfflinePageScript.PolicyMeta + OfflinePageScript.CreateElement("4chan") + "<title>/wg/ - Fixture</title></head>");
            StringAssert.Contains(html, "<body >");
            // The only script left is our own offline script
            OfflinePageScriptTests.AssertHasOfflineScript(html, "4chan");
            Assert.DoesNotContain("var board", html);
            Assert.DoesNotContain("onload", html);
        }

        // With thumbnails off the page is otherwise saved as downloaded: its file links stay live and
        // no thumbnail is downloaded
        [TestMethod]
        public void SavedPageHasNoActiveContentWithThumbnailsOff() {
            Settings.SaveThumbnails = false;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            string html = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(html, "<head>" + OfflinePageScript.PolicyMeta + OfflinePageScript.CreateElement("4chan") + "<title>/wg/ - Fixture</title></head>");
            StringAssert.Contains(html, "<body >");
            // The only script left is our own offline script
            OfflinePageScriptTests.AssertHasOfflineScript(html, "4chan");
            Assert.DoesNotContain("var board", html);
            Assert.DoesNotContain("onload", html);
            StringAssert.Contains(html, "<img src=\"" + server.BaseURL() + "/wg/1700000000001s.jpg\"");
            StringAssert.Contains(html, "href=\"" + server.BaseURL() + "/wg/1700000000001.jpg\"");
            StringAssert.Contains(html, "</html>\r\n");
            Assert.IsEmpty(server.RequestsTo(FourChanThreadFixture.ThumbPaths[0]));
            Assert.IsFalse(Directory.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "thumbs")));
        }

        // The setting is read once per check: turned off after the page is downloaded, the page is
        // still processed with local links
        [TestMethod]
        public void ThumbnailsTurnedOffDuringCheckStillProcessesPage() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            SetSaveThumbnailsAfterPageDownload(watcher, false);

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            string html = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(html, "<img src=\"thumbs/1700000000001s.jpg\"");
            // The only script left is our own offline script
            OfflinePageScriptTests.AssertHasOfflineScript(html, "4chan");
        }

        // Turned on after the page is downloaded, the page has no replace list and is not processed
        [TestMethod]
        public void ThumbnailsTurnedOnDuringCheckLeavesPageUnprocessed() {
            Settings.SaveThumbnails = false;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            SetSaveThumbnailsAfterPageDownload(watcher, true);

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            string html = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(html, "<img src=\"" + server.BaseURL() + "/wg/1700000000001s.jpg\"");
            OfflinePageScriptTests.AssertHasOfflineScript(html, "4chan");
            Assert.IsEmpty(server.RequestsTo(FourChanThreadFixture.ThumbPaths[0]));
        }

        private static void SetSaveThumbnailsAfterPageDownload(ThreadWatcher watcher, bool saveThumbnails) {
            watcher.DownloadStatus += (s, e) => {
                if (e.DownloadType == DownloadType.Page && e.CompleteCount == 1) Settings.SaveThumbnails = saveThumbnails;
            };
        }

        // The second download moves the saved page to the backup, which is deleted once the new
        // page is saved complete
        [TestMethod]
        public void RedownloadWithThumbnailsOffDeletesBackup() {
            Settings.SaveThumbnails = false;
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            RunToStop(CreateWatcher(url));

            ThreadWatcher second = CreateWatcher(url);
            RunToStop(second);

            Assert.HasCount(2, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.IsTrue(File.Exists(SavedPagePath(second)));
            Assert.IsFalse(File.Exists(SavedPagePath(second) + ".bak"));
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

        // S6: a saved login that can't be decrypted is loaded as empty, so no request carries
        // the stored ciphertext or any other credential
        [TestMethod]
        [SupportedOSPlatform("windows")]
        public void UndecryptableSavedLoginSendsNoCredentials() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(PageAuth), Encoding.UTF8.GetBytes("some other app"), DataProtectionScope.CurrentUser);
            string stored = StoredAuth.Prefix + Convert.ToBase64String(blob);
            ThreadInfo thread = ThreadListFile.Parse(new[] { "4", url, stored, stored, "600", "1", "", "", "", "0", "", "", "", "0" }).Threads[0];
            ThreadWatcher watcher = CreateWatcher(url);
            watcher.PageAuth = thread.PageAuth;
            watcher.ImageAuth = thread.ImageAuth;

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(1 + 4 + 3, server.Requests);
            foreach (RecordedRequest request in server.Requests) {
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
