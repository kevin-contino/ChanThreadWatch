using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // MP-7a L3: ctw watch hosts the local API with the app's settings (ApiEnabled, ApiPort). Each test uses a free port
    // found at run time, never the default one, and a token made in its own temporary settings folder. A thread added
    // through the API is on a test host name (registered as 4chan for the test only, or an unknown site under
    // .ctw.test) whose lookup the test answers with a documentation address, and its watcher never connects. Tokens
    // never go into an assertion's message.
    public partial class WatchCommandTests {
        // The .test top-level name is reserved, so it never reaches a real site
        private const string TestDomain = ".ctw.test";
        private const string ApiThreadHost = "api-thread" + TestDomain;
        // TEST-NET-3: public to the SSRF guard, and never connected to (BeforeConnect throws)
        private const string ApiThreadAddress = "203.0.113.10";
        private const string AddedURL = "https://" + ApiThreadHost + "/wg/thread/300";

        private bool _apiThreadHostRegistered;

        private string ApiThreadsPath {
            get { return Path.Combine(Folder, Settings.ApiThreadsFileName); }
        }

        private void TearDownApi() {
            if (_apiThreadHostRegistered) SiteHelpers.UnregisterHostForTesting(ApiThreadHost);
            _apiThreadHostRegistered = false;
            SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
            SSRFGuard.BeforeConnect = addresses => { };
            // Watch_StartFailureOfAnyKindIsAWarning sets it
            OwnerOnlyFile.IsRootOnUnix = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
            WatchApi.ListenPortForTesting = null;
            WatchApi.StopWait = WatchApi.DefaultStopWait;
            ApiPairCommand.PollInterval = ApiPairCommand.DefaultPollInterval;
        }

        // The loopback thread's address is a literal, which is never looked up. lookup stands in for the answer for
        // the test hosts.
        private void UseApiThreadHost(Func<string, IPAddress[]> lookup = null) {
            SiteHelpers.RegisterHostForTesting(ApiThreadHost, typeof(FourChanSiteHelper));
            _apiThreadHostRegistered = true;
            SSRFGuard.ResolveHost = (host, cancellationToken) => {
                if (!host.EndsWith(TestDomain, StringComparison.OrdinalIgnoreCase)) throw new SocketException((int)SocketError.HostNotFound);
                return Task.Run(() => lookup != null ? lookup(host) : new[] { IPAddress.Parse(ApiThreadAddress) });
            };
            SSRFGuard.BeforeConnect = addresses => {
                if (addresses.Contains(IPAddress.Parse(ApiThreadAddress))) throw new InvalidOperationException("The tests never connect to " + ApiThreadAddress + ".");
            };
        }

        // enabled null leaves ApiEnabled out
        private void WriteApiSettings(int port, string enabled = "1", params string[] more) {
            WriteApiSettings(port.ToString(CultureInfo.InvariantCulture), enabled, more);
        }

        private void WriteApiSettings(string port, string enabled, params string[] more) {
            WriteSettings();
            File.AppendAllLines(SettingsPath, new[] { "ApiPort=" + port });
            if (enabled != null) File.AppendAllLines(SettingsPath, new[] { "ApiEnabled=" + enabled });
            File.AppendAllLines(SettingsPath, more);
        }

        private string MakeToken() {
            return new ApiTokenStore(Folder).Generate();
        }

        private static string AddBody(string url) {
            return JsonSerializer.Serialize(new { url });
        }

        [TestMethod]
        public void Watch_WithTheApiOn_ListsAndAddsThreadsWithTheToken() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            string token = MakeToken();
            UseApiThreadHost();
            HeadlessWatch.SaveInterval = TimeSpan.FromMilliseconds(200);
            ApiReply list = null, added = null, unknownSite = null, withoutToken = null;
            HeadlessWatch.StartedForTesting = watch => {
                list = Request(port, HttpMethod.Get, token);
                added = Request(port, HttpMethod.Post, token, AddBody(AddedURL));
                unknownSite = Request(port, HttpMethod.Post, token, AddBody("https://other" + TestDomain + "/thread/1"));
                withoutToken = Request(port, HttpMethod.Get, null);
            };

            CliResult result = RunWatchUntil(() => SavedThread(AddedURL) != null && File.Exists(ApiThreadsPath));

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Error, result.ToString());
            StringAssert.Contains(result.Output, "Local API listening on http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/api/v1/" + Environment.NewLine);
            Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
            using (JsonDocument document = JsonDocument.Parse(list.Body)) {
                CollectionAssert.AreEqual(new[] { ThreadURL }, document.RootElement.GetProperty("threads").EnumerateArray().Select(thread => thread.GetProperty("url").GetString()).ToArray());
            }
            Assert.AreEqual(HttpStatusCode.Created, added.Status, added.Body);
            // Without ApiAllowUnknownHosts=1, a site without a site helper is refused
            AssertProblemCode(unknownSite, "unknown_host");
            Assert.AreEqual(HttpStatusCode.Unauthorized, withoutToken.Status, withoutToken.Body);
            // Saved as guarded: its page ID is in api-threads.txt, beside the thread list
            CollectionAssert.AreEqual(new[] { "1", ThreadUrl.GetPageID(AddedURL) }, File.ReadAllLines(ApiThreadsPath));
            Assert.IsFalse(result.Output.Contains(token, StringComparison.Ordinal), "The token was printed.");
            // The settings saved on stop keep the API's settings, and say that api-threads.txt was written
            string[] settings = File.ReadAllLines(SettingsPath);
            CollectionAssert.Contains(settings, "ApiEnabled=1");
            CollectionAssert.Contains(settings, "ApiPort=" + port.ToString(CultureInfo.InvariantCulture));
            CollectionAssert.Contains(settings, "ApiThreadsFileWritten=1");
        }

        // ApiAllowUnknownHosts=1 lets the API add a thread of a site without a site helper (never an IP address)
        [TestMethod]
        public void Watch_WithUnknownSitesAllowed_AddsAThreadOfAnUnknownSite() {
            int port = FindFreePort();
            WriteApiSettings(port, "1", "ApiAllowUnknownHosts=1");
            WriteThreadList(ThreadLines(ThreadURL));
            string token = MakeToken();
            UseApiThreadHost();
            HeadlessWatch.SaveInterval = TimeSpan.FromMilliseconds(200);
            string unknownURL = "https://other" + TestDomain + "/thread/1";
            ApiReply added = null, address = null;
            HeadlessWatch.StartedForTesting = watch => {
                added = Request(port, HttpMethod.Post, token, AddBody(unknownURL));
                address = Request(port, HttpMethod.Post, token, AddBody("https://" + ApiThreadAddress + "/thread/2"));
            };

            CliResult result = RunWatchUntil(() => SavedThread(unknownURL) != null);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(HttpStatusCode.Created, added.Status, added.Body);
            AssertProblemCode(address, "blocked_host");
            Assert.IsNull(SavedThread("https://" + ApiThreadAddress + "/thread/2"));
        }

        // The load runs on the owner thread; the port opens only once it has returned
        [TestMethod]
        public void Watch_StartsTheApiOnlyAfterTheThreadListIsLoaded() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            bool? listeningWhileLoading = null;
            bool? listeningOnceStarted = null;
            HeadlessWatch.LoadingForTesting = watch => listeningWhileLoading = CanConnect(port);
            HeadlessWatch.StartedForTesting = watch => listeningOnceStarted = CanConnect(port);

            CliResult result = RunWatchUntil(() => listeningOnceStarted.HasValue);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.IsFalse(listeningWhileLoading.Value, "The API listened while the thread list loaded.");
            Assert.IsTrue(listeningOnceStarted.Value, "The API did not listen once the thread list was loaded.");
        }

        // A stop that comes while the thread list loads: the API never starts
        [TestMethod]
        public void Watch_StoppedDuringTheLoad_NeverStartsTheApi() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            bool? listening = null;
            using (CancellationTokenSource stop = new CancellationTokenSource(Timeout)) {
                HeadlessWatch.LoadingForTesting = watch => stop.Cancel();
                HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

                CliResult result = RunWatch(stop.Token);

                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
                Assert.IsFalse(listening.Value, "The API started after the stop.");
                Assert.IsFalse(result.Output.Contains("Local API", StringComparison.Ordinal), result.Output);
            }
        }

        // Missing or "0": no API is made, and watch writes nothing about it
        [TestMethod]
        [DataRow(null)]
        [DataRow("0")]
        public void Watch_WithTheApiOff_ListensOnNoPort(string enabled) {
            int port = FindFreePort();
            WriteApiSettings(port, enabled);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            bool? listening = null;
            HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

            CliResult result = RunWatchUntil(() => listening.HasValue);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(String.Empty, result.Error, result.ToString());
            Assert.IsFalse(listening.Value);
            Assert.IsFalse(result.Output.Contains("Local API", StringComparison.Ordinal), result.Output);
        }

        // Only "1" turns it on, as in the app; any other value but "0" is named in a warning, and the API stays off
        [TestMethod]
        [DataRow("true")]
        [DataRow("1 ")]
        public void Watch_WithAnApiEnabledValueThatIsNotValid_WarnsAndListensOnNoPort(string enabled) {
            int port = FindFreePort();
            WriteApiSettings(port, enabled);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            bool? listening = null;
            HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

            CliResult result = RunWatchUntil(() => listening.HasValue);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("ctw: warning: ApiEnabled in settings.txt is neither 0 nor 1, so the local API stays off. Only ApiEnabled=1 turns it on." + Environment.NewLine,
                result.Error);
            Assert.IsFalse(listening.Value);
        }

        // A port that is not valid is named in a warning, and the default port is used (here a free one stands in for
        // it, so the test never binds the default port)
        [TestMethod]
        [DataRow("80")]
        [DataRow("abc")]
        public void Watch_WithAnApiPortThatIsNotValid_WarnsAndUsesTheDefaultPort(string savedPort) {
            int port = FindFreePort();
            WriteApiSettings(savedPort, "1");
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            int? configuredPort = null;
            WatchApi.ListenPortForTesting = configured => {
                configuredPort = configured;
                return port;
            };
            bool? listening = null;
            HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

            CliResult result = RunWatchUntil(() => listening.HasValue);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("ctw: warning: ApiPort in settings.txt is not a port from 1024 to 65535, so the local API uses port 47710." + Environment.NewLine, result.Error);
            Assert.AreEqual(Settings.DefaultApiPort, configuredPort);
            Assert.IsTrue(listening.Value);
        }

        // Another program has the port: a warning, no other port is tried, and the threads are watched as usual
        [TestMethod]
        public void Watch_WithThePortInUse_WarnsAndKeepsWatching() {
            TcpListener busy = new TcpListener(IPAddress.Loopback, 0);
            busy.Start();
            try {
                int port = ((IPEndPoint)busy.LocalEndpoint).Port;
                WriteApiSettings(port);
                WriteThreadList(ThreadLines(ThreadURL));
                MakeToken();

                CliResult result = RunWatchUntil(ThreadIsDownloaded);

                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
                Assert.AreEqual("ctw: warning: The local API could not start: port " + port.ToString(CultureInfo.InvariantCulture) + " is in use by another program. " +
                    "ctw watch goes on without the local API." + Environment.NewLine, result.Error);
                Assert.IsFalse(result.Output.Contains("Local API listening", StringComparison.Ordinal), result.Output);
                StringAssert.EndsWith(result.Output, "Stopped. The thread list was saved." + Environment.NewLine);
            }
            finally {
                busy.Stop();
            }
        }

        // On, but no token yet: the warning says how to make one, and watch goes on without the API
        [TestMethod]
        public void Watch_WithoutAToken_WarnsToRunApiTokenAndKeepsWatching() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            bool? listening = null;
            HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

            CliResult result = RunWatchUntil(ThreadIsDownloaded);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            StringAssert.StartsWith(result.Error, "ctw: warning: The local API is on (ApiEnabled=1 in settings.txt), but api-token.txt is missing");
            StringAssert.Contains(result.Error, "Run 'ctw api-token' to make a token, then start ctw watch again.");
            Assert.AreEqual(1, Lines(result.Error).Length, result.Error);
            Assert.IsFalse(listening.Value);
            Assert.IsFalse(File.Exists(Path.Combine(Folder, ApiTokenStore.FileName)));
        }

        // A failure that is not a typed one (here the root check throws) is a warning too; the log gets its type only,
        // never its message
        [TestMethod]
        [DataRow(typeof(InvalidOperationException))]
        [DataRow(typeof(IOException))]
        [DataRow(typeof(UnauthorizedAccessException))]
        [DataRow(typeof(NotSupportedException))]
        public void Watch_StartFailureOfAnyKindIsAWarning(Type exceptionType) {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            string marker = "marker-" + Guid.NewGuid().ToString("N");
            OwnerOnlyFile.IsRootOnUnix = () => throw (Exception)Activator.CreateInstance(exceptionType, marker);
            bool? listening = null;
            HeadlessWatch.StartedForTesting = watch => listening = CanConnect(port);

            CliResult result = RunWatchUntil(() => listening.HasValue);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual("ctw: warning: The local API could not start (see log.txt). ctw watch goes on without the local API." + Environment.NewLine, result.Error);
            Assert.IsFalse(listening.Value);
            string log = ReadLog();
            StringAssert.Contains(log, "Local API: ctw watch could not start it: " + exceptionType.FullName);
            Assert.IsFalse(log.Contains(marker, StringComparison.Ordinal), "The exception's message was logged.");
        }

        // BindFailed names a likely cause; the other typed failures are their message
        [TestMethod]
        public void StartFailureWarnings_NameTheCause() {
            Assert.AreEqual("The local API could not listen on port 5000. The port may be reserved by the system; set another ApiPort in settings.txt. " +
                "ctw watch goes on without the local API.", WatchApi.DescribeStartFailure(new ApiStartException(ApiStartError.BindFailed, "The local API could not listen on port 5000.")));
            Assert.AreEqual("The local API does not run as root. Run the program as a normal user to use it. ctw watch goes on without the local API.",
                WatchApi.DescribeStartFailure(new ApiStartException(ApiStartError.Privileged, "The local API does not run as root. Run the program as a normal user to use it.")));
            Assert.AreEqual("The local API was stopped while it started. ctw watch goes on without the local API.",
                WatchApi.DescribeStartFailure(new ApiStartException(ApiStartError.Stopped, "The local API was stopped while it started.")));
        }

        // The stop closes the API first, on its own thread: the port closes while the owner thread is still busy (the
        // stop never waits for it), it is closed by the time the watchers are told to stop, and the run ends
        [TestMethod]
        public void Watch_StopsTheApiFirstWithoutWaitingForTheOwnerThread() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            using (ManualResetEventSlim ownerBusy = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                bool? listeningWhileStopping = null;
                HeadlessWatch.StartedForTesting = watch => BlockOwnerThread(watch, ownerBusy, release);
                HeadlessWatch.StoppingForTesting = watch => listeningWhileStopping = CanConnect(port);
                bool closedWhileOwnerBusy = false;
                Task releaser = Task.Run(() => {
                    WaitFor(() => ownerBusy.IsSet);
                    closedWhileOwnerBusy = WaitFor(() => !CanConnect(port)) && !release.IsSet;
                    release.Set();
                });

                CliResult result = RunWatchUntil(() => ownerBusy.IsSet);

                Assert.IsTrue(releaser.Wait(Timeout));
                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
                Assert.IsTrue(closedWhileOwnerBusy, "The API did not stop while the owner thread was busy.");
                Assert.IsFalse(listeningWhileStopping.Value, "The API still listened when the watchers were told to stop.");
                // The port is free for another program
                TcpListener next = new TcpListener(IPAddress.Loopback, port);
                next.Start();
                next.Stop();
            }
        }

        // An add whose work waits behind a busy owner thread when the stop comes never runs: it gets 503 and the thread
        // is not saved
        [TestMethod]
        public void Watch_StopRefusesAnAddThatWaitsForTheOwnerThread() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            string token = MakeToken();
            using (ManualResetEventSlim ownerBusy = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (ManualResetEventSlim looked = new ManualResetEventSlim(false)) {
                UseApiThreadHost(host => {
                    looked.Set();
                    return new[] { IPAddress.Parse(ApiThreadAddress) };
                });
                Task<ApiReply> add = null;
                HeadlessWatch.StartedForTesting = watch => {
                    BlockOwnerThread(watch, ownerBusy, release);
                    add = Task.Run(() => Request(port, HttpMethod.Post, token, AddBody(AddedURL)));
                    looked.Wait(Timeout);
                };
                Task releaser = Task.Run(() => {
                    WaitFor(() => add != null && add.IsCompleted);
                    release.Set();
                });

                CliResult result = RunWatchUntil(() => looked.IsSet);

                Assert.IsTrue(releaser.Wait(Timeout));
                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, add.Result.Status, add.Result.Body);
                Assert.IsNull(SavedThread(AddedURL));
                Assert.IsFalse(File.Exists(ApiThreadsPath));
            }
        }

        // The stop of the API is not waited for before the first save: a request that holds the stop (its lookup
        // hangs) is still running when the watchers are told to stop, which is after the first save. The stop's wait is
        // made long, so this does not depend on timing.
        [TestMethod]
        public void Watch_FirstSaveIsNotDelayedByARequestThatHoldsTheStop() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            string token = MakeToken();
            WatchApi.StopWait = TimeSpan.FromSeconds(20);
            using (ManualResetEventSlim held = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                UseApiThreadHost(host => {
                    held.Set();
                    release.Wait(Timeout);
                    return new[] { IPAddress.Parse(ApiThreadAddress) };
                });
                Task<ApiReply> add = null;
                bool? addRunningAtFirstSave = null;
                HeadlessWatch.StartedForTesting = watch => {
                    add = Task.Run(() => Request(port, HttpMethod.Post, token, AddBody(AddedURL)));
                    held.Wait(Timeout);
                };
                HeadlessWatch.StoppingForTesting = watch => {
                    addRunningAtFirstSave = !add.IsCompleted;
                    release.Set();
                };

                CliResult result = RunWatchUntil(() => held.IsSet);

                release.Set();
                Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
                Assert.IsTrue(addRunningAtFirstSave.Value, "The first save waited for the API's stop.");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, add.Result.Status, add.Result.Body);
                Assert.IsNull(SavedThread(AddedURL));
            }
        }

        // A stop that throws (here a wait the server refuses, before it stops anything) never skips the saves: the thread
        // list is saved, and then the failure ends the run as a bug. The server it leaves listening is stopped here.
        [TestMethod]
        public void Watch_StopThatThrowsStillSavesTheThreadList() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            WatchApi.StopWait = TimeSpan.FromSeconds(-5);
            ApiServer server = null;
            HeadlessWatch.StartedForTesting = watch => server = watch.Api?.Server;
            try {
                using (CancellationTokenSource stop = new CancellationTokenSource(Timeout + Timeout)) {
                    Task<CliResult> watch = Task.Run(() => RunWatch(stop.Token));
                    bool downloaded = WaitFor(() => ThreadIsDownloaded() || watch.IsCompleted);
                    stop.Cancel();
                    bool ended = ((IAsyncResult)watch).AsyncWaitHandle.WaitOne(Timeout);
                    if (!ended) ResetProcessState();

                    Assert.IsTrue(downloaded && ended, "The run did not download the thread and end.");
                    Assert.IsInstanceOfType<ArgumentOutOfRangeException>(watch.Exception?.InnerException);
                    Assert.AreEqual("127.0.0.1_wg_100", ReadThreadList().Threads.Single().SaveDir);
                }
            }
            finally {
                server?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            Assert.IsNotNull(server, "The API did not start.");
            Assert.IsFalse(CanConnect(port));
        }

        // A new token made while watch runs (as ctw api-token does): the old token stops working and the new one works,
        // without a restart. The command itself is in ApiTokenCommandTests.
        [TestMethod]
        public void Watch_TakesANewTokenWithoutARestart() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            string oldToken = MakeToken();
            ApiReply oldBefore = null, oldAfter = null, newAfter = null;
            HeadlessWatch.StartedForTesting = watch => {
                oldBefore = Request(port, HttpMethod.Get, oldToken);
                string newToken = new ApiTokenStore(Folder).Generate();
                oldAfter = Request(port, HttpMethod.Get, oldToken);
                newAfter = Request(port, HttpMethod.Get, newToken);
            };

            CliResult result = RunWatchUntil(() => newAfter != null);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(HttpStatusCode.OK, oldBefore.Status, oldBefore.Body);
            Assert.AreEqual(HttpStatusCode.Unauthorized, oldAfter.Status, oldAfter.Body);
            Assert.AreEqual(HttpStatusCode.OK, newAfter.Status, newAfter.Body);
        }

        // MP-7a L2b: while api-threads.txt can't be written this session (a folder is in its place), the API refuses
        // to add a thread, which would lose its mark
        [TestMethod]
        public void Watch_RefusesApiAddsWhileTheMarksCannotBeSaved() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            Directory.CreateDirectory(ApiThreadsPath);
            string token = MakeToken();
            UseApiThreadHost();
            ApiReply added = null;
            HeadlessWatch.StartedForTesting = watch => added = Request(port, HttpMethod.Post, token, AddBody(AddedURL));

            CliResult result = RunWatchUntil(() => added != null);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            AssertProblemCode(added, "marks_unavailable");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, added.Status);
            Assert.IsNull(SavedThread(AddedURL));
        }

        // MP-7b L2 end to end: ctw api-pair makes the code, a C# stand-in for the extension pairs with it through the API
        // of ctw watch (hello, the server's proof, finish, the server's proof of the token), the token adds a thread with
        // the extension's Origin, and ctw api-pair reports the pairing. api-pair only reads settings.txt (it never loads
        // the settings into the process), so it can run beside watch here.
        [TestMethod]
        public void Watch_PairsABrowserExtensionWithTheCodeFromApiPair() {
            int port = FindFreePort();
            WriteApiSettings(port);
            WriteThreadList(ThreadLines(ThreadURL));
            MakeToken();
            UseApiThreadHost();
            ApiPairCommand.PollInterval = TimeSpan.FromMilliseconds(50);
            StringWriter pairOutput = new StringWriter(CultureInfo.InvariantCulture);
            TextWriter sharedOutput = TextWriter.Synchronized(pairOutput);
            StringWriter pairError = new StringWriter(CultureInfo.InvariantCulture);
            using (CancellationTokenSource pairStop = new CancellationTokenSource(Timeout + Timeout)) {
                Task<int> pair = Task.Run(() => CliApp.Run(new[] { "api-pair" }, sharedOutput, pairError, Folder, pairStop.Token));
                try {
                    AssertPairsThroughWatch(port, pair, sharedOutput, pairOutput, pairError);
                }
                finally {
                    // A run that did not end must not reach the other tests
                    pairStop.Cancel();
                    ((IAsyncResult)pair).AsyncWaitHandle.WaitOne(Timeout);
                }
            }
        }

        private void AssertPairsThroughWatch(int port, Task<int> pair, TextWriter sharedOutput, StringWriter pairOutput, StringWriter pairError) {
            string code = null;
            Assert.IsTrue(WaitFor(() => (code = FirstLine(sharedOutput, pairOutput)) != null || pair.IsCompleted) && code != null, "ctw api-pair printed no code.");
            TestPairing pairing = null;
            ApiReply added = null;
            HeadlessWatch.StartedForTesting = watch => {
                pairing = TestPairing.Pair(port, code, ApiPairCommandTests.ChromeOrigin);
                added = RequestWithOrigin(port, pairing.Token, ApiPairCommandTests.ChromeOrigin, AddBody(AddedURL));
            };

            CliResult result = RunWatchUntil(() => pairing != null && pair.IsCompleted);

            Assert.AreEqual(CliApp.ExitSuccess, result.ExitCode, result.ToString());
            Assert.AreEqual(CliApp.ExitSuccess, pair.Result, pairError.ToString());
            Assert.IsTrue(pairing.HelloProofMatched, "P1 did not match the printed code.");
            Assert.IsTrue(pairing.FinishProofMatched, "P3 did not match.");
            Assert.AreEqual(WatchApi.ServerName(), pairing.ServerName);
            StringAssert.StartsWith(pairing.ServerName, "ctw watch " + General.Version + " on ");
            Assert.AreEqual(HttpStatusCode.Created, added.Status, added.Body);
            string[] lines;
            lock (sharedOutput) {
                lines = Lines(pairOutput.ToString());
            }
            CollectionAssert.AreEqual(new[] { code, "Paired with the Chrome extension" }, lines);
            Assert.IsFalse(File.Exists(Path.Combine(Folder, ApiPairingFile.FileName)), "ctw api-pair did not delete the pairing file.");
            Assert.AreEqual("chrome", new ApiClientStore(Folder).Read().Single().Family);
            Assert.IsFalse(result.Output.Contains(pairing.Token, StringComparison.Ordinal), "The token was printed.");
        }

        // The first line written so far, or null
        private static string FirstLine(TextWriter shared, StringWriter output) {
            lock (shared) {
                string text = output.ToString();
                int end = text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                return end > 0 ? text.Substring(0, end) : null;
            }
        }

        // A POST of the threads route with a browser's token and Origin
        private static ApiReply RequestWithOrigin(int port, string token, string origin, string json) {
            using (HttpClient client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout })
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + ThreadsEndpoints.ThreadsPath)) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult()) {
                    return new ApiReply { Status = response.StatusCode, Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                }
            }
        }

        // A minimal C# stand-in for the extension's pairing (MP-7b design 3.4), with the proofs from ApiPairing: the
        // key is stretched from the typed code and the salt the hello answered, and each server proof is checked
        private sealed class TestPairing {
            public bool HelloProofMatched { get; private set; }
            public bool FinishProofMatched { get; private set; }
            public string ServerName { get; private set; }
            public string Token { get; private set; }

            public static TestPairing Pair(int port, string code, string origin) {
                TestPairing pairing = new TestPairing();
                string clientNonce = ApiPairing.NewNonce();
                JsonElement hello = Post(port, origin, new { step = "hello", clientNonce });
                string pairingId = hello.GetProperty("pairingId").GetString();
                string serverNonce = hello.GetProperty("serverNonce").GetString();
                pairing.ServerName = hello.GetProperty("serverName").GetString();
                byte[] key = ApiPairing.DeriveKey(code.Replace("-", ""), ApiPairing.FromBase64Url(hello.GetProperty("salt").GetString(), ApiPairing.SaltBytes));
                pairing.HelloProofMatched = ProofMatches(ApiPairing.ServerHelloProof(key, port, origin, pairingId, clientNonce, serverNonce, pairing.ServerName), hello);
                string proof = ApiPairing.Base64Url(ApiPairing.ClientFinishProof(key, port, origin, pairingId, clientNonce, serverNonce));
                JsonElement finish = Post(port, origin, new { step = "finish", pairingId, clientNonce, serverNonce, proof });
                pairing.Token = finish.GetProperty("token").GetString();
                pairing.FinishProofMatched = ProofMatches(ApiPairing.ServerFinishProof(key, port, origin, pairingId, clientNonce, serverNonce, pairing.Token), finish);
                return pairing;
            }

            private static bool ProofMatches(byte[] expected, JsonElement answer) {
                byte[] proof = ApiPairing.FromBase64Url(answer.GetProperty("proof").GetString(), ApiPairing.ProofBytes) ?? new byte[0];
                return CryptographicOperations.FixedTimeEquals(expected, proof);
            }

            // The answer's JSON; anything but 200 fails the test
            private static JsonElement Post(int port, string origin, object body) {
                using (HttpClient client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout })
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + PairingEndpoints.PairingPath)) {
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult()) {
                        string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
                        return JsonDocument.Parse(text).RootElement.Clone();
                    }
                }
            }
        }

        // Holds the owner thread until release is set
        private static void BlockOwnerThread(HeadlessWatch watch, ManualResetEventSlim ownerBusy, ManualResetEventSlim release) {
            watch.Post(() => {
                ownerBusy.Set();
                release.Wait(Timeout);
            });
            ownerBusy.Wait(Timeout);
        }

        private static void AssertProblemCode(ApiReply reply, string code) {
            using (JsonDocument problem = JsonDocument.Parse(reply.Body)) {
                Assert.AreEqual(code, problem.RootElement.GetProperty("code").GetString(), reply.Body);
            }
        }

        // The log that every test's runs write to (CliTestHost opens it first)
        private static string ReadLog() {
            using (FileStream stream = new FileStream(Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        private static int FindFreePort() {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally {
                listener.Stop();
            }
        }

        // The API listens on 127.0.0.1 only
        private static bool CanConnect(int port) {
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork)) {
                try {
                    client.Connect(IPAddress.Loopback, port);
                    return true;
                }
                catch (SocketException) {
                    return false;
                }
            }
        }

        // token null sends no Authorization header
        private static ApiReply Request(int port, HttpMethod method, string token, string json = null) {
            using (HttpClient client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout })
            using (HttpRequestMessage request = new HttpRequestMessage(method, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + ThreadsEndpoints.ThreadsPath)) {
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult()) {
                    return new ApiReply { Status = response.StatusCode, Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                }
            }
        }

        private sealed class ApiReply {
            public HttpStatusCode Status { get; set; }
            public string Body { get; set; }
        }
    }
}
