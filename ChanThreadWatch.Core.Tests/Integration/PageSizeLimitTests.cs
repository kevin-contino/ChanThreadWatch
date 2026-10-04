using System;
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

        private static LoopbackResponse Body(int size, string contentType, bool chunked) {
            byte[] body = Encoding.ASCII.GetBytes(new string('a', size));
            return chunked ? LoopbackResponse.Chunked(body, contentType) : LoopbackResponse.Bytes(body, contentType);
        }
    }
}
