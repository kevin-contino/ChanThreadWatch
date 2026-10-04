using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class ThreadWatcherErrorReportingTests {
        private const string ThreadURL = "http://example.com/b/res/1.html";

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        // A failed TLS handshake is an HttpRequestException (SecureConnectionError) around the
        // AuthenticationException; the shape was recorded from .NET 10.0.12 against a self-signed certificate
        [TestMethod]
        public void DescribesAnUntrustedCertificateWithTheHost() {
            var reject = new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");
            Assert.AreEqual("certificate not trusted for boards.example.org", ThreadWatcher.DescribeDownloadError(TLSFailure(reject), "https://boards.example.org/b/res/1.html"));
        }

        [TestMethod]
        public void DescribesARejectedCertificateWithTheHost() {
            var reject = new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.");
            Assert.AreEqual("certificate not trusted for example.com", ThreadWatcher.DescribeDownloadError(TLSFailure(reject), ThreadURL));
        }

        [TestMethod]
        public void DescribesAFailedSecureChannelWithTheHost() {
            var alert = new AuthenticationException("Authentication failed because the remote party sent a TLS alert: 'ProtocolVersion'.");
            Assert.AreEqual("secure connection failed for example.com", ThreadWatcher.DescribeDownloadError(TLSFailure(alert), ThreadURL));
        }

        private static HttpRequestException TLSFailure(Exception inner) {
            return new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.", inner);
        }

        [TestMethod]
        public void DescribesAHostThatIsNotFoundOrUnreachable() {
            var notFound = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (example.com:80)", new SocketException((int)SocketError.HostNotFound));
            var refused = new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (example.com:80)", new SocketException((int)SocketError.ConnectionRefused));
            Assert.AreEqual("host not found: example.com", ThreadWatcher.DescribeDownloadError(notFound, ThreadURL));
            Assert.AreEqual("cannot connect to example.com", ThreadWatcher.DescribeDownloadError(refused, ThreadURL));
        }

        [TestMethod]
        public void DescribesAnotherSocketFailureAsALostConnection() {
            var ex = new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset));
            Assert.AreEqual("connection lost", ThreadWatcher.DescribeDownloadError(ex, ThreadURL));
        }

        // The server closed the connection before the whole response head arrived
        [TestMethod]
        public void DescribesAResponseThatEndedEarlyAsALostConnection() {
            var ended = new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            var reset = new HttpRequestException(HttpRequestError.Unknown, "An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset)));
            Assert.AreEqual("connection lost", ThreadWatcher.DescribeDownloadError(ended, ThreadURL));
            Assert.AreEqual("connection lost", ThreadWatcher.DescribeDownloadError(reset, ThreadURL));
        }

        // A lookup that found no address, whatever error wraps it
        [TestMethod]
        [DataRow(SocketError.HostNotFound)]
        [DataRow(SocketError.NoData)]
        [DataRow(SocketError.TryAgain)]
        public void DescribesAFailedLookupAsHostNotFound(SocketError error) {
            var lookup = new SocketException((int)error);
            var connect = new HttpRequestException(HttpRequestError.ConnectionError, "No data of the requested type was found. (example.com:80)", lookup);
            var unknown = new HttpRequestException(HttpRequestError.Unknown, "An error occurred.", new IOException("lookup", lookup));
            Assert.AreEqual("host not found: example.com", ThreadWatcher.DescribeDownloadError(connect, ThreadURL));
            Assert.AreEqual("host not found: example.com", ThreadWatcher.DescribeDownloadError(unknown, ThreadURL));
            Assert.AreEqual("host not found: example.com", ThreadWatcher.DescribeDownloadError(lookup, ThreadURL));
        }

        [TestMethod]
        public void DescribesANetworkReadFailureAsALostConnection() {
            Assert.AreEqual("connection lost", ThreadWatcher.DescribeDownloadError(new IOException("reset"), ThreadURL));
        }

        // A socket that times out is not a lost connection
        [TestMethod]
        public void DescribesASocketTimeoutAsATimeout() {
            var timedOut = new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.TimedOut));
            Assert.AreEqual("timed out connecting to example.com", ThreadWatcher.DescribeDownloadError(timedOut, ThreadURL));
        }

        [TestMethod]
        public void DescribesOtherErrorsByTheirMessage() {
            Assert.AreEqual("Timed out while waiting for response", ThreadWatcher.DescribeDownloadError(new TimeoutException("Timed out while waiting for response."), ThreadURL));
            Assert.AreEqual("HTTP 403 Forbidden", ThreadWatcher.DescribeDownloadError(new HTTPStatusException(403, "HTTP 403 Forbidden"), ThreadURL));
            Assert.AreEqual("The server returned an invalid or unrecognized response", ThreadWatcher.DescribeDownloadError(new HttpRequestException(HttpRequestError.InvalidResponse, "The server returned an invalid or unrecognized response."), ThreadURL));
        }

        // B22: a link to a file that is not on disk is made absolute and attribute-encoded
        [TestMethod]
        public void LiveFileAttributeIsAbsoluteAndEncoded() {
            Assert.AreEqual("href=\"https://i.example.com/b/a&amp;b.jpg\"",
                ThreadWatcher.GetLiveFileAttribute("href=\"//i.example.com/b/a&amp;b.jpg\"", "https://boards.example.com/b/res/1.html"));
            Assert.AreEqual("src=\"http://example.com/b/thumb/1s.jpg\"",
                ThreadWatcher.GetLiveFileAttribute("src='/b/thumb/1s.jpg'", ThreadURL));
        }

        [TestMethod]
        public void LiveFileAttributeIsKeptAsIsWithoutABaseURL() {
            Assert.AreEqual("href=\"1.jpg\"", ThreadWatcher.GetLiveFileAttribute("href=\"1.jpg\"", null));
        }

        [TestMethod]
        public void WaitStatusShowsTheCheckErrorOrTheFailedFiles() {
            Assert.AreEqual("Waiting 60 seconds", WatcherStatusText.FormatWaitStatus(60, null, 0));
            Assert.AreEqual("Error: HTTP 403 Forbidden, waiting 60 seconds", WatcherStatusText.FormatWaitStatus(60, "HTTP 403 Forbidden", 2));
            Assert.AreEqual("1 file failed, waiting 5 seconds", WatcherStatusText.FormatWaitStatus(5, null, 1));
            Assert.AreEqual("2 files failed, waiting 5 seconds", WatcherStatusText.FormatWaitStatus(5, null, 2));
        }

        [TestMethod]
        public void StopStatusShowsTheCheckErrorOrTheFailedFiles() {
            Assert.AreEqual("Stopped: Download complete", WatcherStatusText.FormatStopStatus(StopReason.DownloadComplete, null, 0));
            Assert.AreEqual("Stopped: Download complete, 2 files failed", WatcherStatusText.FormatStopStatus(StopReason.DownloadComplete, null, 2));
            Assert.AreEqual("Stopped: Error: certificate not trusted for example.com", WatcherStatusText.FormatStopStatus(StopReason.Other, "certificate not trusted for example.com", 0));
            Assert.AreEqual("Stopped: Unknown error", WatcherStatusText.FormatStopStatus(StopReason.Other, null, 0));
            Assert.AreEqual("Stopped: User requested", WatcherStatusText.FormatStopStatus(StopReason.UserRequest, "HTTP 500 Internal Server Error", 1));
            Assert.AreEqual("Stopped: Page not found", WatcherStatusText.FormatStopStatus(StopReason.PageNotFound, null, 3));
        }

        [TestMethod]
        public void StopStatusShowsTheReparseError() {
            Assert.AreEqual("Stopped: User requested, reparse failed: Access denied", WatcherStatusText.AppendReparseError("Stopped: User requested", "Access denied"));
            Assert.AreEqual("Stopped: User requested", WatcherStatusText.AppendReparseError("Stopped: User requested", null));
            Assert.AreEqual("Stopped: User requested", WatcherStatusText.AppendReparseError("Stopped: User requested", String.Empty));
        }

        // A bug's raw message (e.g. from Path.Combine with a null folder) is not shown; file errors are
        [TestMethod]
        public void ReparseErrorHidesTheMessageOfAnUnexpectedError() {
            Exception bug = Assert.ThrowsExactly<ArgumentNullException>(() => Path.Combine(null, "1.html"));
            Assert.AreEqual("unexpected error, details in the log file", ThreadWatcher.DescribeReparseError(bug));
            Assert.AreEqual("unexpected error, details in the log file", ThreadWatcher.DescribeReparseError(new InvalidOperationException("Sequence contains no elements")));
            Assert.AreEqual("Access to the path is denied", ThreadWatcher.DescribeReparseError(new UnauthorizedAccessException("Access to the path is denied.")));
            Assert.AreEqual("The disk is full", ThreadWatcher.DescribeReparseError(new IOException("The disk is full.\r\n")));
        }

        // B25: a thread added by auto-follow only inherits credentials from a same-origin parent
        [TestMethod]
        public void ChildThreadOnAnotherOriginGetsNoCredentials() {
            ThreadWatcher parent = CreateParent();

            ThreadInfo child = parent.CreateChildThreadInfo("http://other.example.com/b/res/2.html", new DateTime(2026, 1, 2), true);

            Assert.IsNull(child.PageAuth);
            Assert.IsNull(child.ImageAuth);
        }

        [TestMethod]
        public void ChildThreadOnTheSameOriginKeepsCredentials() {
            ThreadWatcher parent = CreateParent();
            var addedOn = new DateTime(2026, 1, 2);

            ThreadInfo child = parent.CreateChildThreadInfo("http://example.com/b/res/2.html", addedOn, false);

            Assert.AreEqual("page:secret", child.PageAuth);
            Assert.AreEqual("image:secret", child.ImageAuth);
            Assert.AreEqual("http://example.com/b/res/2.html", child.URL);
            Assert.AreEqual(120, child.CheckIntervalSeconds);
            Assert.IsTrue(child.OneTimeDownload);
            Assert.AreEqual("cat", child.Category);
            Assert.IsFalse(child.AutoFollow);
            Assert.IsNull(child.StopReason);
            Assert.IsNull(child.SaveDir);
            Assert.AreEqual(String.Empty, child.Description);
            Assert.AreEqual(addedOn, child.ExtraData.AddedOn);
            Assert.AreEqual(parent.PageID, child.ExtraData.AddedFrom);
        }

        [TestMethod]
        public void ChildThreadOnTheSameHostWithAnotherSchemeGetsNoCredentials() {
            ThreadInfo child = CreateParent().CreateChildThreadInfo("https://example.com/b/res/2.html", DateTime.Now, true);

            Assert.IsNull(child.PageAuth);
            Assert.IsNull(child.ImageAuth);
        }

        // R3: the child list can be enumerated while children are added
        [TestMethod]
        public void ChildThreadsCanBeEnumeratedWhileChildrenAreAdded() {
            ThreadWatcher parent = new ThreadWatcher(ThreadURL);
            parent.AddChildThread(new ThreadWatcher("http://example.com/b/res/2.html"));
            int added = 0;

            foreach (ThreadWatcher child in parent.ChildThreads.Values) {
                parent.AddChildThread(new ThreadWatcher("http://example.com/b/res/" + (3 + added++) + ".html"));
            }

            Assert.AreEqual(1, added);
            Assert.HasCount(2, parent.ChildThreads);
            Assert.HasCount(2, parent.DescendantThreads);
        }

        // A child added again for the same page replaces the old one, as the thread list does
        [TestMethod]
        public void AddingAChildForTheSamePageReplacesTheOldOne() {
            ThreadWatcher parent = new ThreadWatcher(ThreadURL);
            ThreadWatcher child = new ThreadWatcher("http://example.com/b/res/2.html");
            ThreadWatcher replacement = new ThreadWatcher("http://example.com/b/res/2.html");

            Assert.IsTrue(parent.AddChildThread(child));
            Assert.IsFalse(parent.AddChildThread(replacement));
            Assert.HasCount(1, parent.ChildThreads);
            Assert.AreSame(replacement, parent.ChildThreads[child.PageID]);
        }

        [TestMethod]
        public void DescendantSlotsAreLimitedUntilReleased() {
            ThreadWatcher.MaxDescendantThreads = 2;
            try {
                ThreadWatcher root = new ThreadWatcher(ThreadURL);
                Assert.IsTrue(root.TryReserveDescendantSlot());
                Assert.IsTrue(root.TryReserveDescendantSlot());
                Assert.IsFalse(root.TryReserveDescendantSlot());

                root.ReleaseDescendantSlot();
                root.AddChildThread(new ThreadWatcher("http://example.com/b/res/2.html"));

                Assert.IsFalse(root.TryReserveDescendantSlot());
                root.ReleaseDescendantSlot();
                Assert.IsTrue(root.TryReserveDescendantSlot());
            }
            finally {
                ThreadWatcher.MaxDescendantThreads = ThreadWatcher.DefaultMaxDescendantThreads;
            }
        }

        // R7
        [TestMethod]
        public void FourChanAndInfinitechanRecognizeThreadPages() {
            Assert.IsTrue(IsThreadPage("https://boards.4chan.org/wg/thread/100", File.ReadAllText(Fixture("4chan-thread.html"))));
            Assert.IsTrue(IsThreadPage("https://8ch.net/tech/res/100.html", File.ReadAllText(Fixture("8ch-thread.html"))));
        }

        // R7
        [TestMethod]
        public void FourChanAndInfinitechanRejectErrorPages() {
            const string banPage = "<html><body><h1>You are banned!</h1><div class=\"post\">reason</div></body></html>";
            Assert.IsFalse(IsThreadPage("https://boards.4chan.org/wg/thread/100", banPage));
            Assert.IsFalse(IsThreadPage("https://8ch.net/tech/res/100.html", banPage));
        }

        // R7: sites whose markup is not known count every page as a thread
        [TestMethod]
        public void OtherSitesCountEveryPageAsAThread() {
            Assert.IsTrue(IsThreadPage("http://example.com/b/res/1.html", "<html><body>Error</body></html>"));
            Assert.IsTrue(IsThreadPage("https://warosu.org/g/thread/123", "<html><body>Error</body></html>"));
        }

        private static bool IsThreadPage(string url, string html) {
            SiteHelper siteHelper = SiteHelpers.GetInstance(new Uri(url).Host);
            siteHelper.SetURL(url);
            siteHelper.SetHTMLParser(new HTMLParser(html));
            return siteHelper.IsThreadPage();
        }

        private static string Fixture(string name) {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);
        }

        private static ThreadWatcher CreateParent() {
            return new ThreadWatcher(ThreadURL) {
                PageAuth = "page:secret",
                ImageAuth = "image:secret",
                CheckIntervalSeconds = 120,
                OneTimeDownload = true,
                Category = "cat"
            };
        }
    }
}
