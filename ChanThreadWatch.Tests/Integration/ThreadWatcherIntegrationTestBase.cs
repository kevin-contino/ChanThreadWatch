using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // Drives the real ThreadWatcher against loopback servers. Each test gets empty settings, its own
    // download folder under %TEMP%, and 127.0.0.1 parsed as a 4chan host. Settings and the
    // watcher's work scheduler are process-global, so these tests must not run in parallel.
    public abstract class ThreadWatcherIntegrationTestBase {
        protected const string PageHost = "127.0.0.1";
        protected static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

        private static readonly Func<string, HTMLParser> _defaultPageParserFactory = ThreadWatcher.PageParserFactory;

        private readonly List<LoopbackHttpServer> _servers = new List<LoopbackHttpServer>();

        protected string DownloadDir { get; private set; }

        [TestInitialize]
        public void SetUpIntegration() {
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
            DownloadDir = Path.Combine(Path.GetTempPath(), "ctw-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DownloadDir);
            Settings.DownloadFolder = DownloadDir;
            Settings.DownloadFolderIsRelative = false;
            SiteHelpers.RegisterHostForTesting(PageHost, typeof(FourChanSiteHelper));
        }

        [TestCleanup]
        public void TearDownIntegration() {
            ThreadWatcher.MaxFileBytes = ThreadWatcher.DefaultMaxFileBytes;
            ThreadWatcher.MaxDescendantThreads = ThreadWatcher.DefaultMaxDescendantThreads;
            ThreadWatcher.PageParserFactory = _defaultPageParserFactory;
            SiteHelpers.UnregisterHostForTesting(PageHost);
            foreach (LoopbackHttpServer server in _servers) server.Dispose();
            _servers.Clear();
            Settings.Load();
            DeleteDirectory(DownloadDir);
        }

        protected LoopbackHttpServer StartServer() {
            var server = new LoopbackHttpServer();
            _servers.Add(server);
            return server;
        }

        protected static ThreadWatcher CreateWatcher(string pageURL) {
            return new ThreadWatcher(pageURL) { OneTimeDownload = true };
        }

        // Starts one check and waits for the watcher to raise StopStatus
        protected static StopReason RunToStop(ThreadWatcher watcher) {
            var stopped = new ManualResetEvent(false);
            StopReason reason = StopReason.Other;
            watcher.StopStatus += (s, e) => { reason = e.StopReason; stopped.Set(); };
            watcher.Start();
            Assert.IsTrue(stopped.WaitOne(RunTimeout), "ThreadWatcher did not stop within " + RunTimeout);
            Assert.IsTrue(watcher.WaitUntilStopped((int)RunTimeout.TotalMilliseconds), "Check did not finish");
            return reason;
        }

        // Runs the given number of checks of a watching (not one-time) watcher back to back, then
        // stops it. Each check after the first starts as soon as the previous one has finished.
        // beforeNextCheck, if given, runs after each check but the last with the 1-based number of
        // the check that finished.
        protected static void RunChecks(ThreadWatcher watcher, int checkCount, System.Action<int> beforeNextCheck = null) {
            var waiting = new AutoResetEvent(false);
            watcher.OneTimeDownload = false;
            watcher.WaitStatus += (s, e) => waiting.Set();
            watcher.Start();
            for (int i = 0; i < checkCount; i++) {
                Assert.IsTrue(waiting.WaitOne(RunTimeout), "Check " + (i + 1) + " did not finish within " + RunTimeout);
                if (i == checkCount - 1) break;
                beforeNextCheck?.Invoke(i + 1);
                watcher.MillisecondsUntilNextCheck = 0;
            }
            watcher.Stop(StopReason.UserRequest);
            Assert.IsTrue(watcher.WaitUntilStopped((int)RunTimeout.TotalMilliseconds), "Check did not finish");
        }

        // The application log, which tests read to check that a failure was logged
        protected static string ReadLog() {
            string path = (string)typeof(Logger).GetField("_logPath", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (!File.Exists(path)) return String.Empty;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        protected static string SavedPagePath(ThreadWatcher watcher) {
            return Path.Combine(watcher.ThreadDownloadDirectory, FourChanThreadFixture.ThreadName + ".html");
        }

        private static void DeleteDirectory(string dir) {
            for (int attempt = 0; attempt < 5 && Directory.Exists(dir); attempt++) {
                try {
                    Directory.Delete(dir, true);
                }
                catch (IOException) {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
