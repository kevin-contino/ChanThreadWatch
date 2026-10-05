using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // How the watcher reports and recovers from failures of the thread page and its files
    [TestClass]
    public class ThreadWatcherErrorHandlingTests : ThreadWatcherIntegrationTestBase {
        private static readonly string FirstImage = FourChanThreadFixture.ImagePaths[0];

        // R5: an HTTP error on the thread page is retried, then reported instead of ending the
        // one-time download as if it had completed
        [TestMethod]
        public void HttpErrorOnThreadPageIsReportedAndLogged() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.StatusOnly(403, "Forbidden"));
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher watcher = CreateWatcher(url);

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.AreEqual("HTTP 403 Forbidden", watcher.CheckError);
            Assert.AreEqual("HTTP 403 Forbidden", watcher.StopError);
            Assert.HasCount(3, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            StringAssert.Contains(ReadLog(), "Error downloading page " + url + ": HTTP 403 Forbidden");
            Assert.IsFalse(File.Exists(SavedPagePath(watcher)));
        }

        // S4: a page over the size limit is reported once and not retried
        [TestMethod]
        public void PageOverTheSizeLimitIsReportedWithoutRetrying() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            General.MaxPageBytes = 1024;
            try {
                string url = server.URL(FourChanThreadFixture.ThreadPath);
                ThreadWatcher watcher = CreateWatcher(url);

                StopReason reason = RunToStop(watcher);

                Assert.AreEqual(StopReason.Other, reason);
                Assert.AreEqual("The page is larger than the maximum of 1024 bytes", watcher.StopError);
                Assert.HasCount(1, server.RequestsTo(FourChanThreadFixture.ThreadPath));
                Assert.IsFalse(File.Exists(SavedPagePath(watcher)));
            }
            finally {
                General.MaxPageBytes = General.DefaultMaxPageBytes;
            }
        }

        // R5: while watching, the error of the last check stays visible until a check succeeds
        [TestMethod]
        public void ServerErrorWhileWatchingIsKeptUntilACheckSucceeds() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()));
            LoopbackResponse error = LoopbackResponse.StatusOnly(500, "Internal Server Error");
            server.RouteSequence(FourChanThreadFixture.ThreadPath, error, error, error, page);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var checkErrors = new List<string>();
            watcher.WaitStatus += (s, e) => checkErrors.Add(watcher.CheckError);

            RunChecks(watcher, 2);

            CollectionAssert.AreEqual(new[] { "HTTP 500 Internal Server Error", null }, checkErrors);
            Assert.IsTrue(File.Exists(SavedPagePath(watcher)));
        }

        // R5: the error of an earlier check is not shown as the reason for a later, unrelated stop
        [TestMethod]
        public void StopForAnotherReasonDoesNotCarryTheCheckError() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.StatusOnly(403, "Forbidden"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var waiting = new ManualResetEvent(false);
            var stopped = new ManualResetEvent(false);
            watcher.OneTimeDownload = false;
            watcher.WaitStatus += (s, e) => waiting.Set();
            watcher.StopStatus += (s, e) => stopped.Set();
            watcher.Start();
            Assert.IsTrue(waiting.WaitOne(RunTimeout));

            watcher.Stop(StopReason.Other);

            Assert.IsTrue(stopped.WaitOne(RunTimeout));
            Assert.AreEqual("HTTP 403 Forbidden", watcher.CheckError);
            Assert.IsNull(watcher.StopError);
        }

        // R5: a certificate that is not trusted is reported with the host rather than retried
        // silently
        [TestMethod]
        public void UntrustedCertificateOnThreadPageIsReported() {
            using (var server = new SelfSignedTlsServer()) {
                string url = server.URL(FourChanThreadFixture.ThreadPath);
                ThreadWatcher watcher = CreateWatcher(url);

                StopReason reason = RunToStop(watcher);

                Assert.AreEqual(StopReason.Other, reason);
                Assert.AreEqual("certificate not trusted for 127.0.0.1", watcher.CheckError);
                StringAssert.Contains(ReadLog(), "Error downloading page " + url + ": certificate not trusted for 127.0.0.1");
            }
        }

        // B23/R5: an image that fails every try is counted and logged, and the run says so
        [TestMethod]
        public void ImageThatFailsEveryTryIsCountedAndLogged() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, LoopbackResponse.StatusOnly(503, "Service Unavailable"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.AreEqual(1, watcher.FailedFileCount);
            Assert.IsNull(watcher.CheckError);
            Assert.HasCount(3, server.RequestsTo(FirstImage));
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(FirstImage) + ": HTTP 503 Service Unavailable");
        }

        // R4: a connection reset while the thread page is read is a network failure and is
        // retried, not a disk error that stops the watcher
        [TestMethod]
        public void ConnectionResetWhileReadingThePageIsRetried() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] page = Encoding.UTF8.GetBytes(fixture.Html(server.BaseURL(), server.BaseURL()));
            server.RouteSequence(FourChanThreadFixture.ThreadPath,
                LoopbackResponse.ResetAfter(page, page.Length / 2, "text/html; charset=utf-8"),
                LoopbackResponse.Bytes(page, "text/html; charset=utf-8"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(2, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            StringAssert.Contains(File.ReadAllText(SavedPagePath(watcher)), "Text only, no file");
        }

        // R4: a connection reset while an image is read is retried, not skipped for good
        [TestMethod]
        public void ConnectionResetWhileReadingAnImageIsRetried() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] image = fixture.Images[FirstImage];
            server.RouteSequence(FirstImage,
                LoopbackResponse.ResetAfter(image, image.Length / 2, "image/jpeg"),
                LoopbackResponse.Bytes(image, "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(2, server.RequestsTo(FirstImage));
            CollectionAssert.AreEqual(image, File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(0, watcher.FailedFileCount);
        }

        // R10: an image that failed is tried again on the next check even though the page did
        // not change (the page would otherwise be answered with 304 and queue nothing), and it
        // is saved under its own name since the failed try did not keep the name reserved
        [TestMethod]
        public void FailedImageIsRetriedOnTheNextCheckUnderItsOwnName() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Last-Modified", "Wed, 01 Jan 2025 00:00:00 GMT");
            server.Route(FourChanThreadFixture.ThreadPath, r => r.Header("If-Modified-Since") != null ? LoopbackResponse.StatusOnly(304, "Not Modified") : page);
            LoopbackResponse error = LoopbackResponse.StatusOnly(500, "Internal Server Error");
            server.RouteSequence(FirstImage, error, error, error, LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 2);

            Assert.HasCount(4, server.RequestsTo(FirstImage));
            Assert.IsNull(server.RequestsTo(FourChanThreadFixture.ThreadPath)[1].Header("If-Modified-Since"));
            CollectionAssert.AreEqual(fixture.Images[FirstImage], File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.IsEmpty(Directory.GetFiles(watcher.ThreadDownloadDirectory, "1700000000001_*"));
            StringAssert.Contains(File.ReadAllText(SavedPagePath(watcher)), "<a class=\"fileThumb\" href=\"1700000000001.jpg\"");
        }

        // R10: a file that keeps failing makes only a few checks download the whole page again;
        // after that the page is requested with If-Modified-Since again
        [TestMethod]
        public void FileThatKeepsFailingStopsForcingPageDownloads() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Last-Modified", "Wed, 01 Jan 2025 00:00:00 GMT");
            server.Route(FourChanThreadFixture.ThreadPath, r => r.Header("If-Modified-Since") != null ? LoopbackResponse.StatusOnly(304, "Not Modified") : page);
            server.Route(FirstImage, LoopbackResponse.StatusOnly(403, "Forbidden"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 5);

            List<RecordedRequest> pageRequests = server.RequestsTo(FourChanThreadFixture.ThreadPath);
            Assert.HasCount(5, pageRequests);
            for (int i = 1; i <= 3; i++) {
                Assert.IsNull(pageRequests[i].Header("If-Modified-Since"), "check " + (i + 1));
            }
            Assert.IsNotNull(pageRequests[4].Header("If-Modified-Since"));
            Assert.HasCount(4 * 3, server.RequestsTo(FirstImage));
            StringAssert.Contains(ReadLog(), "Giving up retrying " + server.URL(FirstImage) + " until the page changes.");
        }

        // R4/R10: a file that can't be written (here: in use) is tried again in a later
        // check rather than skipped for the rest of the session
        [TestMethod]
        public void FileThatCannotBeWrittenIsRetriedInALaterCheck() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse error = LoopbackResponse.StatusOnly(500, "Internal Server Error");
            server.RouteSequence(FirstImage, error, error, error, LoopbackResponse.Bytes(fixture.Images[FirstImage], "image/jpeg"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            string imagePath = Path.Combine(DownloadDir, "127.0.0.1_wg_100", "1700000000001.jpg");
            FileStream blocker = null;
            var failedCounts = new List<int>();
            watcher.WaitStatus += (s, e) => failedCounts.Add(watcher.FailedFileCount);

            try {
                RunChecks(watcher, 3, check => {
                    // Check 1 fails on the server; check 2 finds the file in use; check 3 can write it
                    if (check == 1) blocker = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    if (check == 2) {
                        blocker.Dispose();
                        File.Delete(imagePath);
                    }
                });
            }
            finally {
                blocker?.Dispose();
            }

            CollectionAssert.AreEqual(new[] { 1, 1, 0 }, failedCounts);
            CollectionAssert.AreEqual(fixture.Images[FirstImage], File.ReadAllBytes(imagePath));
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(FirstImage) + ": cannot write file");
        }

        // R10: once nothing failed, the page is requested with If-Modified-Since again
        [TestMethod]
        public void PageIsRequestedConditionallyWhenNothingFailed() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            LoopbackResponse page = LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()))
                .WithHeader("Last-Modified", "Wed, 01 Jan 2025 00:00:00 GMT");
            server.Route(FourChanThreadFixture.ThreadPath, r => r.Header("If-Modified-Since") != null ? LoopbackResponse.StatusOnly(304, "Not Modified") : page);
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunChecks(watcher, 2);

            Assert.IsNotNull(server.RequestsTo(FourChanThreadFixture.ThreadPath)[1].Header("If-Modified-Since"));
        }

        // R7: a 200 OK page that is not a thread (here a ban page) does not overwrite the saved
        // thread and is reported
        [TestMethod]
        public void PageThatIsNotAThreadDoesNotOverwriteTheSavedThread() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher first = CreateWatcher(url);
            RunToStop(first);
            string savedPage = File.ReadAllText(SavedPagePath(first));
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html("<html><body><h1>You are banned!</h1></body></html>"));

            ThreadWatcher second = CreateWatcher(url);
            StopReason reason = RunToStop(second);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.AreEqual("not a thread page", second.CheckError);
            Assert.AreEqual(savedPage, File.ReadAllText(SavedPagePath(second)));
            Assert.IsFalse(File.Exists(SavedPagePath(second) + ".bak"));
            StringAssert.Contains(ReadLog(), "Error downloading page " + url + ": not a thread page");
        }

        // R7: without a saved copy, a page that is not a thread is not kept either
        [TestMethod]
        public void PageThatIsNotAThreadIsNotSavedOnTheFirstCheck() {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html("<html><body>Error: please try again</body></html>"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.IsFalse(File.Exists(SavedPagePath(watcher)));
        }

        // B8: an exception while the downloaded page is parsed ends the check instead of leaving
        // it waiting forever; the page is rejected like one that is not a thread, so the saved
        // copy is kept and the error is shown
        [TestMethod]
        public void PageParseFailureIsReportedAndKeepsTheSavedThread() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher first = CreateWatcher(url);
            RunToStop(first);
            string savedPage = File.ReadAllText(SavedPagePath(first));
            ThreadWatcher.PageParserFactory = html => { throw new InvalidOperationException("parse failed"); };

            ThreadWatcher watcher = CreateWatcher(url);
            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.Other, reason);
            Assert.AreEqual("page could not be read", watcher.CheckError);
            Assert.AreEqual("page could not be read", watcher.StopError);
            Assert.AreEqual(savedPage, File.ReadAllText(SavedPagePath(watcher)));
            Assert.IsFalse(File.Exists(SavedPagePath(watcher) + ".bak"));
            StringAssert.Contains(ReadLog(), "parse failed");
        }

        // R7: when an earlier save was left incomplete, its backup is the last complete copy;
        // a page that is not a thread brings that copy back instead of losing it
        [TestMethod]
        public void PageThatIsNotAThreadRestoresTheBackupOfAnIncompleteSave() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher first = CreateWatcher(url);
            RunToStop(first);
            string pagePath = SavedPagePath(first);
            string completePage = File.ReadAllText(pagePath);
            File.WriteAllText(pagePath + ".bak", completePage);
            File.WriteAllText(pagePath, "<html><body><div class=\"thread\">incomplete");
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html("<html><body><h1>You are banned!</h1></body></html>"));

            ThreadWatcher second = CreateWatcher(url);
            RunToStop(second);

            Assert.AreEqual("not a thread page", second.CheckError);
            Assert.AreEqual(completePage, File.ReadAllText(pagePath));
            Assert.IsFalse(File.Exists(pagePath + ".bak"));
        }

        // When the saved page can't be moved to its backup, the download fails as a disk error
        // and the saved page is not overwritten
        // (on Windows the open handle blocks the move; on Unix the backup locks the page
        // first, and the reader's FileStream holds a shared flock that makes that lock fail)
        [TestMethod]
        public void PageIsNotOverwrittenWhenItsBackupCannotBeMade() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher first = CreateWatcher(url);
            RunToStop(first);
            string pagePath = SavedPagePath(first);
            string savedPage = File.ReadAllText(pagePath);
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()).Replace("OP text", "changed")));

            StopReason reason;
            ThreadWatcher second = CreateWatcher(url);
            // Open without delete sharing, so the page can be written but not moved
            using (new FileStream(pagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                reason = RunToStop(second);
            }

            Assert.AreEqual(StopReason.IOError, reason);
            Assert.AreEqual(savedPage, File.ReadAllText(pagePath));
        }

        // R2: an exception during a reparse is logged and still ends the reparse, so shutdown
        // does not wait for it forever (here the thread has no download folder yet)
        [TestMethod]
        public void ReparseThatFailsStillFinishes() {
            LoopbackHttpServer server = StartServer();
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            watcher.BeginReparse();
            // The reparse runs on a scheduler thread; give it time to start before waiting on it
            Thread.Sleep(500);

            Assert.IsTrue(watcher.WaitReparse(10000), "Reparse did not finish");
            Assert.IsFalse(watcher.IsReparsing);
            StringAssert.Contains(ReadLog(), "Reparse of " + watcher.PageURL + " failed");
        }

        // A failed reparse raises the stop status again with its error, so the UI shows it
        // instead of the reparse progress (here the thread has no download folder yet)
        [TestMethod]
        public void ReparseThatFailsReportsItsError() {
            LoopbackHttpServer server = StartServer();
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var stopped = new ManualResetEvent(false);
            string reparseError = null;
            watcher.StopStatus += (s, e) => { reparseError = ((ThreadWatcher)s).ReparseError; stopped.Set(); };

            watcher.BeginReparse();

            Assert.IsTrue(stopped.WaitOne(10000), "No stop status after the failed reparse");
            Assert.IsFalse(String.IsNullOrEmpty(reparseError));
            Assert.DoesNotContain("\n", reparseError);
        }

        // A reparse that ends early (here there is no saved page) also replaces the reparse
        // progress with the stop status, and clears the error of an earlier failed reparse
        [TestMethod]
        public void ReparseWithoutSavedPageRaisesStopStatusWithoutError() {
            LoopbackHttpServer server = StartServer();
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            var stopped = new AutoResetEvent(false);
            var reparseErrors = new List<string>();
            watcher.StopStatus += (s, e) => { reparseErrors.Add(((ThreadWatcher)s).ReparseError); stopped.Set(); };
            watcher.BeginReparse();
            Assert.IsTrue(stopped.WaitOne(10000), "No stop status after the failed reparse");
            watcher.ThreadDownloadDirectory = Path.Combine(DownloadDir, "empty");

            watcher.BeginReparse();

            Assert.IsTrue(stopped.WaitOne(10000), "No stop status after the reparse without a saved page");
            Assert.IsNotNull(reparseErrors[0]);
            Assert.IsNull(reparseErrors[1]);
        }

        // B9: when the poster folder can't be created, the watcher stops with a disk error and
        // the image meant for that folder is not downloaded
        [TestMethod]
        public void PosterFolderThatCannotBeCreatedStopsWithoutDownloadingIntoIt() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            Settings.SortImagesByPoster = true;
            string threadDir = Path.Combine(DownloadDir, "127.0.0.1_wg_100");
            Directory.CreateDirectory(threadDir);
            // A file where the poster folder of "Bob!Trip" would go
            File.WriteAllText(Path.Combine(threadDir, "Bob!Trip"), "not a folder");
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.IOError, reason);
            Assert.IsEmpty(server.RequestsTo(FourChanThreadFixture.ImagePaths[1]));
        }

        // B19: a file whose URL gives no file name (here a thumbnail URL ending in "/..") is
        // skipped instead of being saved over its folder
        [TestMethod]
        public void FileWithoutAFileNameIsSkipped() {
            LoopbackHttpServer server = StartServer();
            server.Route("/b/res/123.html", LoopbackResponse.Html("<html><body><a href=\"/b/src/1.jpg\"><img src=\"/b/thumb/..\"></a></body></html>"));
            server.Route("/b/src/1.jpg", LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(1, 500), "image/jpeg"));
            server.Route("/b/", LoopbackResponse.Bytes(FourChanThreadFixture.MakeBytes(2, 100), "image/jpeg"));
            // localhost is not a known site, so the generic site helper parses the page
            ThreadWatcher watcher = CreateWatcher(server.URL("/b/res/123.html", "localhost"));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1.jpg")));
            Assert.IsEmpty(server.RequestsTo("/b/"));
        }

        // B22: an image deleted from the server (404) keeps its link to the image online, not a
        // link to a local file that does not exist
        [TestMethod]
        public void SavedPageLinksToTheImageOnlineWhenItWasNotSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FirstImage, LoopbackResponse.StatusOnly(404, "Not Found"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            string html = File.ReadAllText(SavedPagePath(watcher));
            StringAssert.Contains(html, "<a class=\"fileThumb\" href=\"" + server.URL(FirstImage) + "\" target=\"_blank\">");
            StringAssert.Contains(html, "<a class=\"fileThumb\" href=\"1700000000002.png\"");
        }

        // B20: a child thread's folder is renamed to include the parent's description even when
        // its name already equals the child's own description
        [TestMethod]
        public void ChildThreadFolderIsRenamedToIncludeTheParentDescription() {
            Settings.RenameDownloadFolderWithDescription = true;
            Settings.RenameDownloadFolderWithParentThreadDescription = true;
            Settings.ParentThreadDescriptionFormat = " ({Parent})";
            ThreadWatcher parent = new ThreadWatcher("http://127.0.0.1:1/wg/thread/100");
            parent.Description = "Parent";
            ThreadWatcher child = new ThreadWatcher("http://127.0.0.1:1/wg/thread/200") { ParentThread = parent };
            string childDir = Path.Combine(DownloadDir, "Child");
            Directory.CreateDirectory(childDir);
            child.ThreadDownloadDirectory = childDir;

            child.Description = "Child";

            Assert.AreEqual(Path.Combine(DownloadDir, "Child (Parent)"), child.ThreadDownloadDirectory);
            Assert.IsTrue(Directory.Exists(child.ThreadDownloadDirectory));
            Assert.IsFalse(Directory.Exists(childDir));
        }

        // A settings file without the parent description format (e.g. after a hand edit) uses
        // the default format instead of failing
        [TestMethod]
        public void ChildThreadFolderUsesTheDefaultParentFormatWhenTheSettingIsMissing() {
            Settings.RenameDownloadFolderWithDescription = true;
            Settings.RenameDownloadFolderWithParentThreadDescription = true;
            Settings.ParentThreadDescriptionFormat = null;
            ThreadWatcher parent = new ThreadWatcher("http://127.0.0.1:1/wg/thread/100");
            parent.Description = "Parent";
            ThreadWatcher child = new ThreadWatcher("http://127.0.0.1:1/wg/thread/200") { ParentThread = parent };
            string childDir = Path.Combine(DownloadDir, "Child");
            Directory.CreateDirectory(childDir);
            child.ThreadDownloadDirectory = childDir;

            child.Description = "Child";

            Assert.AreEqual(" (Parent)", child.ParentThreadFormattedDescription);
            Assert.AreEqual(Path.Combine(DownloadDir, "Child (Parent)"), child.ThreadDownloadDirectory);
        }

        // B29: a cross-link that is not a valid URL is skipped; the valid ones are still followed
        [TestMethod]
        public void InvalidCrossLinkIsSkipped() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            SiteHelpers.RegisterHostForTesting(PageHost, typeof(InvalidCrossLinkSiteHelper));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.AutoFollow = true;
            var added = new List<string>();
            watcher.AddThread += (s, e) => { lock (added) added.Add(e.PageURL); };

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            CollectionAssert.AreEqual(new[] { InvalidCrossLinkSiteHelper.ValidLink }, added);
        }

        // S4: auto-follow adds at most MaxDescendantThreads threads, and logs when it stops
        [TestMethod]
        public void AutoFollowStopsAtTheThreadLimit() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            var links = new StringBuilder();
            for (int i = 201; i <= 205; i++) {
                links.AppendFormat("<a href=\"/wg/thread/{0}#p{0}\" class=\"quotelink\">&gt;&gt;{0}</a><br>", i);
            }
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(fixture.Html(server.BaseURL(), server.BaseURL()).Replace("OP text", links.ToString())));
            ThreadWatcher.MaxDescendantThreads = 3;
            string url = server.URL(FourChanThreadFixture.ThreadPath);
            ThreadWatcher watcher = CreateWatcher(url);
            watcher.AutoFollow = true;
            var added = new List<string>();
            watcher.AddThread += (s, e) => { lock (added) added.Add(e.PageURL); };

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(3, added);
            StringAssert.Contains(ReadLog(), "Auto-follow limit of 3 threads reached for " + url);
        }

        // S4: threads raised for adding count against the limit until the handler releases them,
        // so checks that run before the UI has added the threads cannot pass the limit
        [TestMethod]
        public void AutoFollowLimitHoldsAcrossChecksBeforeThreadsAreAdded() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(PageWithCrossLinks(fixture, server, 201, 205)));
            ThreadWatcher.MaxDescendantThreads = 3;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.AutoFollow = true;
            var added = new List<string>();
            // The handler never adds or releases, like a UI thread that has not caught up
            watcher.AddThread += (s, e) => { lock (added) added.Add(e.PageURL); };

            RunChecks(watcher, 3);

            Assert.HasCount(3, added);
        }

        // S4: once the threads are added, the limit counts them instead of the reservations
        [TestMethod]
        public void AutoFollowLimitCountsAddedThreads() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html(PageWithCrossLinks(fixture, server, 201, 205)));
            ThreadWatcher.MaxDescendantThreads = 3;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            watcher.AutoFollow = true;
            var added = new List<string>();
            watcher.AddThread += (s, e) => {
                lock (added) added.Add(e.PageURL);
                watcher.AddChildThread(new ThreadWatcher(e.PageURL) { ParentThread = watcher });
                watcher.RootThread.ReleaseDescendantSlot();
            };

            RunChecks(watcher, 3);

            Assert.HasCount(3, added);
            Assert.HasCount(3, watcher.DescendantThreads);
        }

        private static string PageWithCrossLinks(FourChanThreadFixture fixture, LoopbackHttpServer server, int first, int last) {
            var links = new StringBuilder();
            for (int i = first; i <= last; i++) {
                links.AppendFormat("<a href=\"/wg/thread/{0}#p{0}\" class=\"quotelink\">&gt;&gt;{0}</a><br>", i);
            }
            return fixture.Html(server.BaseURL(), server.BaseURL()).Replace("OP text", links.ToString());
        }

        // S4: a file exactly at the limit is saved
        [TestMethod]
        public void FileExactlyAtTheLimitIsSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] image = fixture.Images[FirstImage];
            server.Route(FirstImage, new LoopbackResponse { RawBytes = CloseDelimited(image) });
            ThreadWatcher.MaxFileBytes = image.Length;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            CollectionAssert.AreEqual(image, File.ReadAllBytes(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(0, watcher.FailedFileCount);
        }

        [TestMethod]
        public void FileAnnouncedExactlyAtTheLimitIsSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher.MaxFileBytes = fixture.Images[FirstImage].Length;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            Assert.IsTrue(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(0, watcher.FailedFileCount);
        }

        private static byte[] CloseDelimited(byte[] body) {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nConnection: close\r\n\r\n");
            byte[] raw = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, raw, 0, head.Length);
            Buffer.BlockCopy(body, 0, raw, head.Length, body.Length);
            return raw;
        }

        // S4: a file announced as larger than the limit is not downloaded
        [TestMethod]
        public void FileAnnouncedAsTooLargeIsNotSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            ThreadWatcher.MaxFileBytes = 10000;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.HasCount(1, server.RequestsTo(FirstImage));
            Assert.IsFalse(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(1, watcher.FailedFileCount);
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(FirstImage) + ": file is larger than the limit of 10000 bytes");
        }

        // S4: a file without a Content-Length is aborted once its body passes the limit
        [TestMethod]
        public void FileWhoseBodyExceedsTheLimitIsNotSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nConnection: close\r\n\r\n");
            byte[] image = fixture.Images[FirstImage];
            byte[] raw = new byte[head.Length + image.Length];
            Buffer.BlockCopy(head, 0, raw, 0, head.Length);
            Buffer.BlockCopy(image, 0, raw, head.Length, image.Length);
            server.Route(FirstImage, new LoopbackResponse { RawBytes = raw });
            ThreadWatcher.MaxFileBytes = 10000;
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason);
            Assert.IsFalse(File.Exists(Path.Combine(watcher.ThreadDownloadDirectory, "1700000000001.jpg")));
            Assert.AreEqual(1, watcher.FailedFileCount);
            Assert.HasCount(1, server.RequestsTo(FirstImage));
            StringAssert.Contains(ReadLog(), "Error downloading file " + server.URL(FirstImage) + ": file is larger than the limit of 10000 bytes");
        }

        // S4: a huge announced size is only partly preallocated on disk
        [TestMethod]
        public void PreallocationIsCappedWhateverTheServerAnnounces() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            const long announced = 100L * 1024 * 1024;
            server.Route(FirstImage, new LoopbackResponse {
                RawBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: " + announced + "\r\nConnection: close\r\n\r\nshort body")
            });
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            string path = Path.Combine(DownloadDir, "127.0.0.1_wg_100", "1700000000001.jpg");
            long largestPreallocation = 0;
            var sync = new object();
            watcher.DownloadStart += (s, e) => {
                if (e.URL != server.URL(FirstImage)) return;
                long length = new FileInfo(path).Length;
                lock (sync) largestPreallocation = Math.Max(largestPreallocation, length);
            };

            RunToStop(watcher);

            Assert.AreEqual(64L * 1024 * 1024, largestPreallocation);
        }

        // Reports one invalid cross-link and one valid one for every page
        public class InvalidCrossLinkSiteHelper : FourChanSiteHelper {
            public const string ValidLink = "http://127.0.0.1:1/wg/thread/300";

            public override HashSet<string> GetCrossLinks(List<ReplaceInfo> replaceList, bool interBoardAutoFollow) {
                return new HashSet<string> { "http://[not a url/wg/thread/1", ValidLink };
            }
        }
    }
}
