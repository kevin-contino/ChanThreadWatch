using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // MP-5b acceptance: the page size limit (General.MaxPageBytes) applies to every page response,
    // whatever its content type and whether its length is announced, and to the page a meta refresh
    // leads to. Before, only an HTML page was limited while it was buffered.
    [TestClass]
    public class PageSizeLimitTests : ThreadWatcherIntegrationTestBase {
        private const int Limit = 1024;
        private const string TooLarge = "The page is larger than the maximum of 1024 bytes";

        [TestInitialize]
        public void UseASmallLimit() {
            General.MaxPageBytes = Limit;
        }

        [TestCleanup]
        public void RestoreTheLimit() {
            General.MaxPageBytes = General.DefaultMaxPageBytes;
        }

        [TestMethod]
        [DataRow("application/json", false)]
        [DataRow("application/json", true)]
        [DataRow("text/plain", false)]
        [DataRow("application/octet-stream", true)]
        public void APageOfAnyContentTypeOverTheLimitIsReported(string contentType, bool chunked) {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, Body(Limit + 1, contentType, chunked));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            Assert.AreEqual(StopReason.Other, RunToStop(watcher));

            Assert.AreEqual(TooLarge, watcher.StopError);
            Assert.HasCount(1, server.Requests);
        }

        // The meta refresh page is small; the page it leads to is over the limit
        [TestMethod]
        [DataRow("text/html; charset=utf-8", false)]
        [DataRow("text/html; charset=utf-8", true)]
        [DataRow("application/json", false)]
        public void APageReachedThroughAMetaRefreshOverTheLimitIsReported(string contentType, bool chunked) {
            LoopbackHttpServer server = StartServer();
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=/target\"></head></html>"));
            server.Route("/target", Body(Limit + 1, contentType, chunked));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            Assert.AreEqual(StopReason.Other, RunToStop(watcher));

            Assert.AreEqual(TooLarge, watcher.StopError);
            Assert.HasCount(1, server.RequestsTo("/target"));
        }

        // A page at the limit is not cut off: the watcher gets the whole of it and saves it
        [TestMethod]
        public void APageReachedThroughAMetaRefreshAtTheLimitIsSaved() {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            General.MaxPageBytes = Encoding.UTF8.GetByteCount(fixture.Html(String.Empty, String.Empty));
            server.Route(FourChanThreadFixture.ThreadPath, LoopbackResponse.Html("<html><head><meta http-equiv=\"refresh\" content=\"0; URL=/target\"></head></html>"));
            server.Route("/target", LoopbackResponse.Html(fixture.Html(String.Empty, String.Empty)));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));

            RunToStop(watcher);

            Assert.IsNull(watcher.StopError);
            Assert.IsNull(watcher.CheckError);
            Assert.HasCount(1, server.RequestsTo("/target"));
        }

        // A later check that gets a page over the limit leaves the page saved by the check before it
        // as it was, and no backup behind; with and without an announced length
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void APageOverTheLimitKeepsThePreviouslySavedPage(bool chunked) {
            var fixture = new FourChanThreadFixture();
            LoopbackHttpServer server = StartServer();
            fixture.RouteAll(server);
            string html = fixture.Html(server.BaseURL(), server.BaseURL());
            byte[] page = Encoding.UTF8.GetBytes(html);
            General.MaxPageBytes = page.Length;
            byte[] larger = Encoding.UTF8.GetBytes(html + new string(' ', 4096));
            server.RouteSequence(FourChanThreadFixture.ThreadPath, LoopbackResponse.Bytes(page, "text/html; charset=utf-8"),
                chunked ? LoopbackResponse.Chunked(larger, "text/html; charset=utf-8") : LoopbackResponse.Bytes(larger, "text/html; charset=utf-8"));
            ThreadWatcher watcher = CreateWatcher(server.URL(FourChanThreadFixture.ThreadPath));
            string savedPage = null;

            RunChecks(watcher, 2, check => savedPage = File.ReadAllText(SavedPagePath(watcher)));

            Assert.HasCount(2, server.RequestsTo(FourChanThreadFixture.ThreadPath));
            Assert.AreEqual("The page is larger than the maximum of " + page.Length + " bytes", watcher.CheckError);
            Assert.IsNotNull(savedPage);
            Assert.AreEqual(savedPage, File.ReadAllText(SavedPagePath(watcher)));
            Assert.IsFalse(File.Exists(SavedPagePath(watcher) + ".bak"));
        }

        private static LoopbackResponse Body(int size, string contentType, bool chunked) {
            byte[] body = Encoding.ASCII.GetBytes(new string('a', size));
            return chunked ? LoopbackResponse.Chunked(body, contentType) : LoopbackResponse.Bytes(body, contentType);
        }
    }
}
