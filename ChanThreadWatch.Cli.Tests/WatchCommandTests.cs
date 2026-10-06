using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JDP.Tests;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // ctw watch in this process against the loopback test server and the sterile 4chan thread, with a temporary
    // settings folder and an absolute temporary download folder. Every run is stopped by a token with a timeout, never
    // by the process's signals. Saved logins go through FakeLoginStore, never DPAPI or a login store.
    [TestClass]
    public partial class WatchCommandTests : CliTestBase {
        private const string PageHost = "127.0.0.1";
        private const string KeychainValue = "keychain:0123456789abcdef0123456789abcdef";
        private const string UnusedKeychainValue = "keychain:aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbb";
        private const string NotFoundPath = "/wg/thread/404";
        private const string ChildPath = "/wg/thread/200";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly MethodInfo _onAddThread = typeof(ThreadWatcher).GetMethod("OnAddThread", BindingFlags.NonPublic | BindingFlags.Instance);
        // Taken before any test puts the fake one in its place
        private static Func<IStoredAuthProtector> _defaultProtectorFactory;

        private FakeLoginStore _loginStore;
        private LoopbackHttpServer _server;
        private FourChanThreadFixture _fixture;

        [ClassInitialize]
        public static void TakeDefaultProtectorFactory(TestContext context) {
            _defaultProtectorFactory = WatchCommand.CreateProtector;
        }

        [TestInitialize]
        public void SetUpWatch() {
            _loginStore = new FakeLoginStore();
            WatchCommand.CreateProtector = () => _loginStore;
            SiteHelpers.RegisterHostForTesting(PageHost, typeof(FourChanSiteHelper));
            // Requests are not spaced out, so the test stays fast
            ConnectionManager.MinRequestStartIntervalMS = 0;
            _fixture = new FourChanThreadFixture();
            _server = new LoopbackHttpServer();
            _fixture.RouteAll(_server);
        }

        [TestCleanup]
        public void TearDownWatch() {
            _server?.Dispose();
            SiteHelpers.UnregisterHostForTesting(PageHost);
            ConnectionManager.ResetForTesting();
            ConnectionManager.MinRequestStartIntervalMS = ConnectionManager.DefaultMinRequestStartIntervalMS;
            if (_defaultProtectorFactory != null) WatchCommand.CreateProtector = _defaultProtectorFactory;
            HeadlessWatch.SaveInterval = HeadlessWatch.DefaultSaveInterval;
            HeadlessWatch.LoadingForTesting = null;
            HeadlessWatch.StartedForTesting = null;
            HeadlessWatch.StoppingForTesting = null;
            HeadlessWatch.WatchersListedForTesting = null;
            HeadlessWatch.WaitForWatcher = HeadlessWatch.DefaultWaitForWatcher;
            StoredAuth.TakeScheduledDeletes();
            TearDownApi();
            ResetProcessState();
            // The settings of the last run stay loaded; the other tests start from none
            Settings.Load(Path.Combine(Folder, "missing-settings.txt"));
        }

        // What a run that did not end leaves set
        private static void ResetProcessState() {
            Settings.SettingsDirectoryOverride = null;
            Settings.RelativeFolderBaseOverride = null;
            StoredAuth.Protector = new KeptStoredAuthProtector();
        }

        private string ThreadURL {
            get { return _server.URL(FourChanThreadFixture.ThreadPath); }
        }

        private string DownloadFolder {
            get { return Path.Combine(Folder, "downloads"); }
        }

        private string SettingsPath {
            get { return Path.Combine(Folder, Settings.SettingsFileName); }
        }

        // An absolute download folder under the test's folder, which must exist (watch refuses a missing one)
        private void WriteSettings() {
            Directory.CreateDirectory(Path.Combine(DownloadFolder, DebugFolder));
            File.WriteAllLines(SettingsPath, new[] { "DownloadFolder=" + DownloadFolder, "DownloadFolderIsRelative=0" });
            Settings.Load(SettingsPath);
            StringAssert.StartsWith(Settings.AbsoluteDownloadDirectory, Folder);
        }

        [TestMethod]
        public void Watch_DownloadsTheThreadSavesTheListAndReleasesTheLock() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL, description: "Loopback thread"));

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Error, result.ToString());
            StringAssert.StartsWith(result.Output, "Watching 1 thread (1 running), settings folder: " + Folder + ". Press Ctrl+C to stop.");
            StringAssert.EndsWith(result.Output, "Stopped. The thread list was saved." + Environment.NewLine);
            AssertThreadFilesDownloaded();
            // The settings were saved on stop, with the download folder as it was
            CollectionAssert.Contains(File.ReadAllLines(SettingsPath), "DownloadFolder=" + DownloadFolder);
            // Saved as the app saves it on exit: still watched (no stop reason), with its folder relative to the download folder
            ThreadInfo saved = ReadThreadList().Threads.Single();
            Assert.AreEqual(ThreadURL, saved.URL);
            Assert.IsNull(saved.StopReason);
            Assert.AreEqual("127.0.0.1_wg_100", saved.SaveDir);
            Assert.IsNotNull(saved.ExtraData.LastImageOn);
            // The lock is let go, and its record says ctw watch held it
            Assert.AreEqual(SettingsFolderLockHolder.Watch, SettingsFolderLock.ReadHolder(Folder).Kind);
            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            appLock.Dispose();
        }

        // The changed thread list is saved on the save interval while watch runs, not only when it stops
        [TestMethod]
        public void Watch_SavesTheThreadListOnTheSaveIntervalWhileRunning() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            HeadlessWatch.SaveInterval = TimeSpan.FromMilliseconds(200);

            CliResult result = RunWatchUntil(() => ThreadIsDownloaded() && SavedThread(ThreadURL)?.SaveDir == "127.0.0.1_wg_100");

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
        }

        // A missing download folder is refused, as is one that is not set or is an absolute path of another OS (a
        // portable folder used on Windows and on Linux or macOS); the app would put the default folder in Documents in
        // its place, and save that in settings.txt unless the setting is another OS's
        [TestMethod]
        [DataRow("missing", " does not exist. Create it, or set another one in the app's settings, then try again.")]
        [DataRow("unset", "No download folder is set in settings.txt.")]
        [DataRow("foreign", "Set a download folder for this system in the app's settings, then try again.")]
        public void Watch_RefusesAMissingDownloadFolderAndChangesNothing(string kind, string message) {
            string missing = Path.Combine(Folder, "missing");
            string foreign = OperatingSystem.IsWindows() ? "/home/user/Threads" : @"C:\Threads";
            Dictionary<string, string[]> settings = new Dictionary<string, string[]> {
                { "missing", new[] { "DownloadFolder=" + missing, "DownloadFolderIsRelative=0" } },
                { "unset", new[] { "CheckEvery=5" } },
                { "foreign", new[] { "DownloadFolder=" + foreign, "DownloadFolderIsRelative=0" } }
            };
            File.WriteAllLines(SettingsPath, settings[kind]);
            WriteThreadList(ThreadLines(ThreadURL));
            byte[] settingsBefore = File.ReadAllBytes(SettingsPath);
            byte[] threadsBefore = File.ReadAllBytes(ThreadListPath);

            CliResult result = RunWatch(TimeoutToken());

            AssertFailed(result, CliApp.ExitFailure, message);
            if (kind == "missing") StringAssert.Contains(result.Error, "The download folder " + missing + " does not exist.");
            CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(SettingsPath));
            CollectionAssert.AreEqual(threadsBefore, File.ReadAllBytes(ThreadListPath));
            Assert.IsFalse(Directory.Exists(missing));
            Assert.IsFalse(Directory.Exists(WatchSession.GetDefaultFolder("Watched Threads")));
            Assert.HasCount(0, _server.Requests);
        }

        // A thread that a watched thread links to is followed: added, watched and saved with its parent
        [TestMethod]
        public void Watch_AddsAndSavesAnAutoFollowedThread() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            HeadlessWatch.SaveInterval = TimeSpan.FromMilliseconds(200);
            string childURL = _server.URL(ChildPath);
            HeadlessWatch.StartedForTesting = watch => RaiseAddThread(watch, ThreadURL, childURL);

            CliResult result = RunWatchUntil(() => SavedThread(childURL)?.StopReason == StopReason.PageNotFound);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Output, "Added " + childURL + Environment.NewLine);
            ThreadInfo child = ReadThreadList().Threads.Single(thread => thread.URL == childURL);
            Assert.AreEqual(new ThreadWatcher(ThreadURL).PageID, child.ExtraData.AddedFrom);
        }

        // A thread that is gone is saved as not found, and its line says so
        [TestMethod]
        public void Watch_SavesAThreadThatIsNotFound() {
            WriteSettings();
            string url = _server.URL(NotFoundPath);
            WriteThreadList(ThreadLines(url));
            HeadlessWatch.SaveInterval = TimeSpan.FromMilliseconds(200);

            CliResult result = RunWatchUntil(() => SavedThread(url)?.StopReason == StopReason.PageNotFound);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Output, url + ": Stopped: Page not found" + Environment.NewLine);
            Assert.AreEqual(StopReason.PageNotFound, ReadThreadList().Threads.Single().StopReason);
        }

        // A thread that work queued before the stop adds (as auto-follow does) is stopped, waited for and saved. The add
        // runs after the stop has listed the watchers it waits for, so only listing them again finds it.
        [TestMethod]
        public void Watch_WaitsForAndSavesAThreadAddedWhileStopping() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            string childURL = _server.URL(ChildPath);
            HeadlessWatch watch = null;
            List<string> waitedFor = new List<string>();
            HeadlessWatch.WatchersListedForTesting = w => {
                if (watch != null) return;
                watch = w;
                w.Post(() => Assert.IsTrue(w.Session.AddThread(NewThread(childURL))));
            };
            HeadlessWatch.WaitForWatcher = (watcher, wait) => {
                lock (waitedFor) waitedFor.Add(watcher.PageURL);
                return HeadlessWatch.DefaultWaitForWatcher(watcher, wait);
            };

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            CollectionAssert.Contains(waitedFor, childURL);
            Assert.HasCount(2, watch.Session.ThreadWatchers);
            Assert.IsTrue(watch.Session.ThreadWatchers.All(w => !w.IsRunning && w.WaitUntilStopped(0)));
            CollectionAssert.AreEquivalent(new[] { ThreadURL, childURL }, ReadThreadList().Threads.Select(thread => thread.URL).ToArray());
        }

        // A watcher that does not stop holds up the stop for StopWait at most; the thread list is then saved anyway
        [TestMethod]
        public void Watch_SavesTheThreadListWhenAWatcherDoesNotStop() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            HeadlessWatch.WaitForWatcher = (watcher, wait) => false;

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Output, ThreadURL + ": did not stop within " + (int)HeadlessWatch.StopWait.TotalSeconds + " seconds; the thread list is saved without waiting for it.");
            StringAssert.EndsWith(result.Output, "Stopped. The thread list was saved." + Environment.NewLine);
            Assert.AreEqual("127.0.0.1_wg_100", ReadThreadList().Threads.Single().SaveDir);
        }

        // After a check in which a file failed all its tries, its line says so (the file is in the log)
        [TestMethod]
        public void Watch_ReportsAFileThatFailedAfterAllItsTries() {
            WriteSettings();
            string failingImage = FourChanThreadFixture.ImagePaths[1];
            _server.Route(failingImage, LoopbackResponse.StatusOnly(500, "Internal Server Error"));
            WriteThreadList(ThreadLines(ThreadURL));
            LockedStringWriter output = new LockedStringWriter();

            CliResult result = RunWatchUntil(() => output.ToString().Contains(ThreadURL + ": 1 file failed, waiting ", StringComparison.Ordinal), output);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.HasCount(3, _server.RequestsTo(failingImage));
            Assert.IsFalse(result.Output.Contains("download failed", StringComparison.Ordinal));
        }

        // A settings file that can't be read is refused, as ctw add does, rather than replaced by the defaults
        [TestMethod]
        public void Watch_RefusesASettingsFileThatCannotBeRead() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            byte[] settingsBefore = File.ReadAllBytes(SettingsPath);
            CliResult result;
            using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                result = RunWatch(TimeoutToken());
            }

            AssertFailed(result, CliApp.ExitFailure, "settings.txt could not be read, so ctw watch does not start: ");
            CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(SettingsPath));
            // Nothing was copied aside, and nothing was downloaded
            CollectionAssert.AreEquivalent(new[] { Settings.SettingsFileName, Settings.ThreadsFileName, SettingsFolderLock.FileName },
                Directory.GetFiles(Folder).Select(Path.GetFileName).ToArray());
            Assert.HasCount(0, _server.Requests);
        }

        // An error output that can't be written (a closed pipe) neither fails the watch nor changes its exit code
        [TestMethod]
        public void Watch_DropsErrorLinesThatCannotBeWritten() {
            _loginStore.IsAvailable = false;
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL, pageAuth: "user:secret"));

            CliResult warned = RunWatchUntil(() => true, error: new ClosedWriter());

            Assert.AreEqual(CliApp.ExitSuccess, warned.ExitCode, warned.ToString());
            File.Delete(ThreadListPath);
            Directory.CreateDirectory(ThreadListPath);
            CliResult failedSave = RunWatchUntil(() => true, error: new ClosedWriter());
            Assert.AreEqual(CliApp.ExitFailure, failedSave.ExitCode, failedSave.ToString());
            StringAssert.EndsWith(failedSave.Output, "Stopped." + Environment.NewLine);
        }

        // The final save fails when threads.txt can't be written (here it is a folder): exit code 1 and a message
        [TestMethod]
        public void Watch_ReportsAFinalSaveThatFailed() {
            WriteSettings();
            Directory.CreateDirectory(ThreadListPath);

            CliResult result = RunWatchUntil(() => true);

            Assert.AreEqual(CliApp.ExitFailure, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Error, "ctw: The thread list could not be saved when ctw watch stopped.");
            StringAssert.EndsWith(result.Output, "Stopped." + Environment.NewLine);
        }

        // Where no login store can be used, a plaintext login of an older version is used for the session and cleared
        // at the next save, as the app does; watch says so first
        [TestMethod]
        public void Watch_WarnsThatPlaintextLoginsAreClearedWithoutALoginStore() {
            _loginStore.IsAvailable = false;
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL, pageAuth: "user:secret"));

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "ctw: warning: threads.txt holds logins saved without encryption by an older version.");
            Assert.IsFalse(result.Error.Contains("secret", StringComparison.Ordinal));
            Assert.AreEqual("user:secret", _server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].BasicAuth);
            Assert.AreEqual(String.Empty, File.ReadAllLines(ThreadListPath)[2]);
        }

        [TestMethod]
        public void PlaintextLogins_AreFoundInEitherFile() {
            WriteThreadList(ThreadLines(ThreadURL));
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=user:secret" });

            CollectionAssert.AreEqual(new[] { Settings.SettingsFileName }, WatchCommand.GetFilesWithPlaintextLogins(Folder));
            File.WriteAllLines(SettingsPath, new[] { "PageAuth=" + KeychainValue });
            Assert.HasCount(0, WatchCommand.GetFilesWithPlaintextLogins(Folder));
        }

        // watch puts the app's saved-login backend in place (here the fake one): a stored login is decrypted and sent,
        // written back as the same reference, and a login store item that no file refers to any more is deleted
        // after the save, as the app does
        [TestMethod]
        public void Watch_UsesSavedLoginsThroughTheAppsBackend() {
            WriteSettings();
            _loginStore.Items[KeychainValue] = "pageuser:pagepass";
            WriteThreadList(ThreadLines(ThreadURL, pageAuth: KeychainValue));
            StoredAuth.ScheduleDelete(UnusedKeychainValue);

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("pageuser:pagepass", _server.RequestsTo(FourChanThreadFixture.ThreadPath)[0].BasicAuth);
            Assert.AreEqual(KeychainValue, File.ReadAllLines(ThreadListPath)[2]);
            CollectionAssert.AreEqual(new[] { UnusedKeychainValue }, _loginStore.Deleted);
            // The command line's own backend is back for the other commands
            Assert.IsInstanceOfType<KeptStoredAuthProtector>(StoredAuth.Protector);
        }

        [TestMethod]
        public void Watch_UsesTheAppsBackendForThisSystem() {
            Assert.AreEqual(typeof(StoredAuth).GetMethod("CreateSystemProtector", BindingFlags.NonPublic | BindingFlags.Static), _defaultProtectorFactory.Method);
        }

        [TestMethod]
        public void Watch_RefusesWhileTheAppHoldsTheLock() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            byte[] before = File.ReadAllBytes(ThreadListPath);
            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            using (appLock) {
                CliResult result = RunWatch(TimeoutToken());

                AssertFailed(result, CliApp.ExitFailure, "Chan Thread Watch (pid " + Environment.ProcessId + " on ");
                StringAssert.Contains(result.Error, " is using the settings folder " + Folder + ". Close it, then start ctw watch again.");
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
            Assert.HasCount(0, _server.Requests);
            Assert.AreEqual(0, _loginStore.Calls);
        }

        [TestMethod]
        public void Watch_RefusesWhileAnotherWatchHoldsTheLock() {
            WriteSettings();
            SettingsFolderLock watchLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.Watch, out watchLock));
            using (watchLock) {
                CliResult result = RunWatch(TimeoutToken());

                AssertFailed(result, CliApp.ExitFailure, "ctw watch (pid " + Environment.ProcessId + " on ");
                StringAssert.Contains(result.Error, SettingsFolderAccess.StopWatchAdvice);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void Watch_RefusesWhileTheAppHasTheFolderOpenOnThisComputer() {
            WriteSettings();
            using (new Mutex(false, SettingsFolderLock.GetAppMutexName(Folder))) {
                CliResult result = RunWatch(TimeoutToken());

                AssertFailed(result, CliApp.ExitFailure, "Chan Thread Watch is running with the settings folder " + Folder + " on this computer. Close it, then start ctw watch again.");
            }
            SettingsFolderLock appLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.WinForms, out appLock));
            appLock.Dispose();
        }

        [TestMethod]
        public void AddAndRemove_RefuseWhileWatchHoldsTheLock() {
            WriteThreadList(ThreadLines("https://boards.4chan.org/a/thread/111"));
            byte[] before = File.ReadAllBytes(ThreadListPath);
            string expected = "ctw watch (pid " + Environment.ProcessId + " on " + SettingsFolderLockHolder.GetThisMachineName() + ") is using the settings folder " + Folder + ". " +
                SettingsFolderAccess.StopWatchAdvice;
            SettingsFolderLock watchLock;
            Assert.IsTrue(SettingsFolderLock.TryAcquire(Folder, SettingsFolderLockHolder.Watch, out watchLock));
            using (watchLock) {
                AssertFailed(Run("add", "https://boards.4chan.org/b/thread/222"), CliApp.ExitFailure, expected);
                AssertFailed(Run("remove", "https://boards.4chan.org/a/thread/111"), CliApp.ExitFailure, expected);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ThreadListPath));
        }

        // MP-7a L2b: with api-threads.txt unreadable, every thread is guarded for this session: the loopback thread is
        // never connected to, watch says so on stderr, and the save writes no mark from the unreadable file
        [TestMethod]
        public void Watch_WithAnUnreadableApiThreadsFile_GuardsEveryThreadForTheSession() {
            WriteSettings();
            WriteThreadList(ThreadLines(ThreadURL));
            string apiThreads = Path.Combine(Folder, Settings.ApiThreadsFileName);
            File.WriteAllLines(apiThreads, new[] { "2", "4chan/a/1" });
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);

            CliResult result = RunWatchUntil(() => output.ToString().Contains(ThreadURL + ": ", StringComparison.Ordinal), output);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "ctw: warning: " + Settings.ApiThreadsFileName + " could not be used (it can't be read, is not valid, or is missing although it was written before), so for this session every thread is watched as one added through the local API");
            StringAssert.Contains(result.Output, "blocked");
            Assert.AreEqual(0, _server.ConnectionCount);
            CollectionAssert.AreEqual(new[] { "1" }, File.ReadAllLines(apiThreads));
            Assert.HasCount(1, Directory.GetFiles(Folder, TextFile.GetCopySearchPattern(apiThreads)));
        }

        // Round 2: api-threads.txt is missing although the settings say it was written: guarded for the session like
        // one that can't be read, with the same warning, and no empty file is made to hide it
        [TestMethod]
        public void Watch_WithAMissingApiThreadsFileThatWasWritten_GuardsEveryThreadForTheSession() {
            WriteSettings();
            File.AppendAllLines(SettingsPath, new[] { "ApiThreadsFileWritten=1" });
            WriteThreadList(ThreadLines(ThreadURL));
            StringWriter output = new StringWriter(CultureInfo.InvariantCulture);

            CliResult result = RunWatchUntil(() => output.ToString().Contains(ThreadURL + ": ", StringComparison.Ordinal), output);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "ctw: warning: " + Settings.ApiThreadsFileName + " could not be used");
            StringAssert.Contains(result.Output, "blocked");
            Assert.AreEqual(0, _server.ConnectionCount);
            Assert.IsFalse(File.Exists(Path.Combine(Folder, Settings.ApiThreadsFileName)));
        }

        [TestMethod]
        public void Watch_TakesNoArguments() {
            AssertFailed(Run("watch", "extra"), CliApp.ExitUsage, "Wrong arguments. Usage: ctw watch");
            CliResult help = Run("watch", "--help");
            AssertSucceeded(help);
            Assert.AreEqual("Usage: ctw watch" + Environment.NewLine, help.Output);
        }

        // In the application data, ctw can't know the app's folder, which relative folders are based on
        [TestMethod]
        [DataRow("DownloadFolder")]
        [DataRow("CompletedFolder")]
        public void RelativeFolderWithSettingsInTheApplicationData_IsRefused(string settingName) {
            File.WriteAllLines(SettingsPath, new[] { settingName + "=rel", settingName + "IsRelative=1" });
            Settings.Load(SettingsPath);

            CliException ex = Assert.ThrowsExactly<CliException>(WatchCommand.EnsureNoRelativeFolder);
            StringAssert.Contains(ex.Message, "relative to the app's folder");
        }

        // Stops the run even if a test never cancels it
        private static CancellationToken TimeoutToken() {
            return new CancellationTokenSource(Timeout).Token;
        }

        private CliResult RunWatch(CancellationToken stopToken, TextWriter output = null, TextWriter error = null) {
            output = output ?? new StringWriter(CultureInfo.InvariantCulture);
            error = error ?? new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = CliApp.Run(new[] { "watch" }, output, error, Folder, stopToken);
            return new CliResult { ExitCode = exitCode, Output = output.ToString(), Error = error.ToString() };
        }

        // Runs ctw watch until the condition holds (or the timeout), then stops it as Ctrl+C would
        private CliResult RunWatchUntil(Func<bool> condition, TextWriter output = null, TextWriter error = null) {
            using (CancellationTokenSource stop = new CancellationTokenSource(Timeout + Timeout)) {
                Task<CliResult> watch = Task.Run(() => RunWatch(stop.Token, output, error));
                bool met = WaitFor(() => condition() || watch.IsCompleted);
                stop.Cancel();
                if (!watch.Wait(Timeout)) {
                    // Whatever the run left set must not reach the other tests
                    ResetProcessState();
                    Assert.Fail("ctw watch did not stop within " + Timeout);
                }
                Assert.IsTrue(met && condition(), "The condition was not met within " + Timeout + ": " + watch.Result);
                return watch.Result;
            }
        }

        private static bool WaitFor(Func<bool> condition) {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!condition()) {
                if (elapsed.Elapsed > Timeout) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        // The thread as threads.txt holds it now (null if it isn't there yet); read as the app's other readers do
        private ThreadInfo SavedThread(string url) {
            string[] lines = SharedFile.ReadAllLines(ThreadListPath);
            return lines != null ? ThreadListFile.Parse(lines).Threads.FirstOrDefault(thread => thread.URL == url) : null;
        }

        // Raises AddThread on the parent the way a check does, which reserves a slot under the root first
        private static void RaiseAddThread(HeadlessWatch watch, string parentURL, string childURL) {
            ThreadWatcher parent;
            Assert.IsTrue(watch.Session.TryGetThreadWatcher(new ThreadWatcher(parentURL).PageID, out parent));
            Assert.IsTrue(parent.RootThread.TryReserveDescendantSlot());
            _onAddThread.Invoke(parent, new object[] { new AddThreadEventArgs(childURL) });
        }

        private static ThreadInfo NewThread(string url) {
            return new ThreadInfo {
                URL = url,
                PageAuth = String.Empty,
                ImageAuth = String.Empty,
                CheckIntervalSeconds = 300,
                Description = String.Empty,
                Category = String.Empty
            };
        }

        private string ThreadFolder {
            get { return Path.Combine(DownloadFolder, DebugFolder, "127.0.0.1_wg_100"); }
        }

        // A Debug build downloads into a Debug folder (see Settings.AbsoluteDownloadDirectory)
        private static string DebugFolder {
#if DEBUG
            get { return Settings.DebugFolderName; }
#else
            get { return String.Empty; }
#endif
        }

        // Every image and thumbnail has its bytes, and the page was saved
        private bool ThreadIsDownloaded() {
            return HasBytes(_fixture.Images, ThreadFolder) && HasBytes(_fixture.Thumbs, Path.Combine(ThreadFolder, "thumbs")) &&
                File.Exists(Path.Combine(ThreadFolder, FourChanThreadFixture.ThreadName + ".html"));
        }

        private static bool HasBytes(Dictionary<string, byte[]> files, string folder) {
            return files.All(file => ReadOrNull(Path.Combine(folder, FourChanThreadFixture.FileName(file.Key)))?.SequenceEqual(file.Value) == true);
        }

        // Null while the file is missing or still being written
        private static byte[] ReadOrNull(string path) {
            try {
                return File.ReadAllBytes(path);
            }
            catch (IOException) {
                return null;
            }
            catch (UnauthorizedAccessException) {
                return null;
            }
        }

        private void AssertThreadFilesDownloaded() {
            foreach (KeyValuePair<string, byte[]> image in _fixture.Images) {
                CollectionAssert.AreEqual(image.Value, File.ReadAllBytes(Path.Combine(ThreadFolder, FourChanThreadFixture.FileName(image.Key))), image.Key);
            }
            foreach (KeyValuePair<string, byte[]> thumb in _fixture.Thumbs) {
                CollectionAssert.AreEqual(thumb.Value, File.ReadAllBytes(Path.Combine(ThreadFolder, "thumbs", FourChanThreadFixture.FileName(thumb.Key))), thumb.Key);
            }
            Assert.HasCount(4, Directory.GetFiles(ThreadFolder, "17*"));
        }

        // Output the test reads while ctw watch writes it from other threads
        private sealed class LockedStringWriter : StringWriter {
            private readonly object _sync = new object();

            public LockedStringWriter() : base(CultureInfo.InvariantCulture) {
            }

            public override void Write(char value) {
                lock (_sync) base.Write(value);
            }

            public override void Write(string value) {
                lock (_sync) base.Write(value);
            }

            public override void WriteLine(string value) {
                lock (_sync) base.WriteLine(value);
            }

            public override string ToString() {
                lock (_sync) return base.ToString();
            }
        }

        // Stands in for the system's login store: references to items it holds, in the Keychain form. When it is not
        // available it behaves like UnavailableStoredAuthProtector.
        private sealed class FakeLoginStore : IStoredAuthProtector {
            public readonly Dictionary<string, string> Items = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> Deleted = new List<string>();
            public bool IsAvailable = true;
            public int Calls;

            public bool CanProtect {
                get { Calls++; return IsAvailable; }
            }

            // A login that changes keeps its item, as the real stores do
            public string Protect(string line, string previousStored) {
                Calls++;
                if (!IsAvailable) return String.Empty;
                string stored = previousStored != null && Items.ContainsKey(previousStored) ? previousStored : "keychain:" + Guid.NewGuid().ToString("N");
                Items[stored] = line;
                return stored;
            }

            public string Unprotect(string stored) {
                Calls++;
                string login;
                return Items.TryGetValue(stored, out login) ? login : String.Empty;
            }

            public void Delete(string stored) {
                Calls++;
                Deleted.Add(stored);
                Items.Remove(stored);
            }
        }
    }
}
