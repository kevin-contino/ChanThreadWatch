using System;
using System.Collections.Generic;
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
using System.Windows.Forms;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // MP-7a L4: the app hosts the local API (LocalApiHost) on a UI thread like the window's (a message loop, and a
    // control's BeginInvoke as the post delegate), with a real Kestrel on a free port found at run time (never the
    // default one), a temporary settings folder (Settings.SettingsDirectoryOverride) and a token made there. A thread
    // added through the API is on a test host name registered as 4chan for the test only; the test answers its lookup
    // with a documentation address, and its watcher never connects. Tokens never go into an assertion's message.
    [TestClass]
    public class LocalApiHostTests {
        // The .test top-level name is reserved, so it never reaches a real site
        private const string TestDomain = ".ctw.test";
        private const string ApiThreadHost = "api-thread" + TestDomain;
        // TEST-NET-3: public to the SSRF guard, and never connected to (BeforeConnect throws)
        private const string ApiThreadAddress = "203.0.113.10";
        private const string AddedURL = "https://" + ApiThreadHost + "/wg/thread/300";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private string _folder;
        private TestUiThread _ui;
        private WatchSession _session;
        private LocalApiHost _host;
        private string _token;
        private Version _savedHostVersion;
        private Action<Action> _post;
        // The test's DNS lookup for the test hosts; it can hold a request
        private Func<string, IPAddress[]> _lookup;
        private readonly NewThreadChoices _choices = new NewThreadChoices { CheckIntervalSeconds = 0, OneTimeDownload = true, AutoFollow = true, Category = "from-window" };

        private string SettingsPath {
            get { return Path.Combine(_folder, Settings.SettingsFileName); }
        }

        [TestInitialize]
        public void SetUp() {
            // As the app at startup: the server name a pairing browser sees holds the app's version
            _savedHostVersion = General.HostVersion;
            Program.SetHostVersion();
            _folder = Path.Combine(Path.GetTempPath(), "ctw-app-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_folder, "downloads"));
            Settings.SettingsDirectoryOverride = _folder;
            Settings.Load();
            Settings.DownloadFolder = Path.Combine(_folder, "downloads");
            Settings.DownloadFolderIsRelative = false;
            SiteHelpers.RegisterHostForTesting(ApiThreadHost, typeof(FourChanSiteHelper));
            _lookup = host => new[] { IPAddress.Parse(ApiThreadAddress) };
            SSRFGuard.ResolveHost = (host, cancellationToken) => {
                if (!host.EndsWith(TestDomain, StringComparison.OrdinalIgnoreCase)) throw new SocketException((int)SocketError.HostNotFound);
                return Task.Run(() => _lookup(host));
            };
            SSRFGuard.BeforeConnect = addresses => throw new InvalidOperationException("The local API tests never connect.");
            _ui = new TestUiThread();
            // As the window: no folder given, so it follows the settings folder
            _session = new WatchSession(_ui.Invoke, _ui.Post);
            _post = _ui.Post;
            _host = new LocalApiHost(_session, work => _post(work), url => _choices.CreateApiThread(url));
            _token = new ApiTokenStore(_folder).Generate();
        }

        [TestCleanup]
        public void TearDown() {
            try {
                Task stopped = _ui.Invoke(() => _host.StopForExit());
                Assert.IsTrue(stopped.Wait(Timeout), "The API did not stop.");
                StopWatchers();
                _ui.Dispose();
            }
            finally {
                SiteHelpers.UnregisterHostForTesting(ApiThreadHost);
                SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
                SSRFGuard.BeforeConnect = addresses => { };
                OwnerOnlyFile.NewFileCheckForTesting = null;
                OwnerOnlyFile.IsRootOnUnix = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
                LocalApiHost.StopWait = LocalApiHost.DefaultStopWait;
                Settings.Load(Path.Combine(_folder, "missing-settings.txt"));
                Settings.SettingsDirectoryOverride = null;
                General.HostVersion = _savedHostVersion;
                DeleteFolder(_folder);
            }
        }

        private void StopWatchers() {
            List<ThreadWatcher> watchers = _session.ThreadWatchers;
            foreach (ThreadWatcher watcher in watchers) {
                watcher.Stop(StopReason.Exiting);
            }
            foreach (ThreadWatcher watcher in watchers) {
                watcher.WaitUntilStopped(10000);
                watcher.WaitReparse(10000);
            }
        }

        // As the window: the load runs off the UI thread, then the window tells the host on the UI thread
        private void LoadThreadList() {
            _session.LoadThreadList();
            _session.LoadBlacklist();
            _ui.Invoke(_host.OnThreadListLoaded);
            WaitForStart();
        }

        private void Apply(bool restartIfNotListening = false) {
            _ui.Invoke(() => _host.Apply(restartIfNotListening));
            WaitForStart();
        }

        private void WaitForStart() {
            Assert.IsTrue(_host.LastStartForTesting.Wait(Timeout), "The start did not end.");
        }

        private static void EnableApi(int port) {
            Settings.ApiEnabled = true;
            Settings.ApiPort = port;
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("0")]
        [DataRow("true")]
        public void OffByDefault_NoServerIsMade(string enabled) {
            if (enabled != null) WriteSettingsAndLoad("ApiEnabled=" + enabled);

            LoadThreadList();

            Assert.AreEqual(LocalApiState.Off, _host.Status.State);
            Assert.IsNull(_host.ServerForTesting);
            Assert.AreEqual(enabled == "true" ? "Off: ApiEnabled in settings.txt is neither 0 nor 1. Only 1 turns the local API on." : "Off", _host.Status.Text);
        }

        // An Apply before the load (the window never calls one then) does not start it; the end of the load does
        [TestMethod]
        public void StartsOnlyOnceTheThreadListIsLoaded() {
            int port = FindFreePort();
            EnableApi(port);

            Apply();

            Assert.IsNull(_host.ServerForTesting);
            Assert.IsFalse(CanConnect(port), "The API listened before the thread list was loaded.");

            LoadThreadList();

            Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
            Assert.AreEqual("Listening on 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), _host.Status.Text);
            Assert.AreEqual(HttpStatusCode.OK, Request(port, HttpMethod.Get, _token).Status);
        }

        // A new port applies without a restart: the old port closes and the new one opens. Applying the same settings
        // again keeps the server.
        [TestMethod]
        public void ApplyRestartsOnAPortChangeAndKeepsAServerForTheSameSettings() {
            int firstPort = FindFreePort();
            EnableApi(firstPort);
            LoadThreadList();
            ApiServer first = _host.ServerForTesting;

            Apply();
            Assert.AreSame(first, _host.ServerForTesting);

            int secondPort = FindFreePort();
            Settings.ApiPort = secondPort;
            Apply();

            Assert.AreNotSame(first, _host.ServerForTesting);
            Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
            Assert.AreEqual("Listening on 127.0.0.1:" + secondPort.ToString(CultureInfo.InvariantCulture), _host.Status.Text);
            Assert.IsTrue(WaitFor(() => !CanConnect(firstPort)), "The old port is still open.");
            Assert.AreEqual(HttpStatusCode.OK, Request(secondPort, HttpMethod.Get, _token).Status);

            Settings.ApiEnabled = false;
            Apply();

            Assert.AreEqual(LocalApiState.Off, _host.Status.State);
            Assert.IsTrue(WaitFor(() => !CanConnect(secondPort)), "The API still listens after it was turned off.");
        }

        // A move of the settings folder (frmSettings) takes api-token.txt along; the next Apply restarts the server on
        // the same port with the new folder's token file, once the old server has freed the port
        [TestMethod]
        public void ApplyAfterASettingsFolderMoveUsesTheNewFolderOnTheSamePort() {
            int port = FindFreePort();
            EnableApi(port);
            LoadThreadList();
            ApiServer first = _host.ServerForTesting;
            string newFolder = Path.Combine(_folder, "moved");
            Directory.CreateDirectory(newFolder);
            Settings.Save();

            frmSettings.MoveSettingsFiles(_folder, newFolder);
            Settings.SettingsDirectoryOverride = newFolder;
            Apply();

            Assert.AreNotSame(first, _host.ServerForTesting);
            Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
            Assert.AreEqual(HttpStatusCode.OK, Request(port, HttpMethod.Get, _token).Status);
            Assert.IsFalse(File.Exists(Path.Combine(_folder, ApiTokenStore.FileName)));
            // A token made in the new folder works there, so the server reads that folder's file
            string newToken = new ApiTokenStore(newFolder).Generate();
            Assert.AreEqual(HttpStatusCode.OK, Request(port, HttpMethod.Get, newToken).Status);
        }

        // Another program has the port: no message, the status says why in plain words, and the log has the same text
        // without the token. An Apply with the same settings tries again once the port is free.
        [TestMethod]
        public void PortInUseIsKeptAsStatusTextAndTriedAgainOnApply() {
            TcpListener busy = new TcpListener(IPAddress.Loopback, 0);
            busy.Start();
            int port = ((IPEndPoint)busy.LocalEndpoint).Port;
            try {
                EnableApi(port);
                LoadThreadList();

                Assert.AreEqual(LocalApiState.Failed, _host.Status.State);
                Assert.AreEqual("Not running: port " + port.ToString(CultureInfo.InvariantCulture) + " is in use by another program. Choose another port.", _host.Status.Text);
                string log = ReadLog();
                StringAssert.Contains(log, "Local API: " + _host.Status.Text);
                Assert.IsFalse(log.Contains(_token, StringComparison.Ordinal), "The token was logged.");
            }
            finally {
                busy.Stop();
            }

            Apply();

            Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
        }

        // On, but no token yet: the status says to make one; once there is one, Apply starts the API
        [TestMethod]
        public void MissingTokenIsKeptAsStatusTextAndANewTokenStartsTheApiOnApply() {
            File.Delete(Path.Combine(_folder, ApiTokenStore.FileName));
            int port = FindFreePort();
            EnableApi(port);

            LoadThreadList();

            Assert.AreEqual(LocalApiState.Failed, _host.Status.State);
            Assert.AreEqual("Not running: there is no token, or its file can be read by others. Click \"New token...\" to make one.", _host.Status.Text);
            Assert.IsFalse(CanConnect(port));

            string token = new ApiTokenStore(_folder).Generate();
            Apply();

            Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
            Assert.AreEqual(HttpStatusCode.OK, Request(port, HttpMethod.Get, token).Status);
        }

        // A failure of a type the API's start does not expect (here the root check throws one) still ends as Failed,
        // never "Starting" for good, and the log has its type only, never its message
        [TestMethod]
        public void AnUnexpectedStartFailureIsFailedAndLogsItsTypeOnly() {
            string marker = "marker-" + Guid.NewGuid().ToString("N");
            OwnerOnlyFile.IsRootOnUnix = () => throw new FormatException(marker);
            EnableApi(FindFreePort());

            LoadThreadList();

            Assert.AreEqual(LocalApiState.Failed, _host.Status.State);
            Assert.AreEqual("Not running: the local API could not start (see log.txt).", _host.Status.Text);
            string log = ReadLog();
            StringAssert.Contains(log, "Local API: Not running: the local API could not start (see log.txt). (System.FormatException)");
            Assert.IsFalse(log.Contains(marker, StringComparison.Ordinal), "The exception's message was logged.");
        }

        // ApiPort in the file is not valid: one log line, and the default port is used
        [TestMethod]
        public void APortThatIsNotValidIsLoggedOnce() {
            WriteSettingsAndLoad("ApiPort=80");

            LoadThreadList();

            Assert.AreEqual(1, CountOccurrences(ReadLog(), "Local API: ApiPort in settings.txt is not a port from 1024 to 65535, so port 47710 is used."));
        }

        // "New token..." while a start is still running (it waits for the stop of the previous server, which a request
        // holds) replaces that start with one that reads the new token; a plain Apply keeps the running start
        [TestMethod]
        public void ApplyAfterANewTokenReplacesAStartThatIsStillRunning() {
            int firstPort = FindFreePort();
            EnableApi(firstPort);
            LocalApiHost.StopWait = TimeSpan.FromSeconds(20);
            LoadThreadList();
            using (ManualResetEventSlim held = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                _lookup = host => {
                    held.Set();
                    release.Wait(Timeout);
                    return new[] { IPAddress.Parse(ApiThreadAddress) };
                };
                Task<ApiReply> add = Task.Run(() => Request(firstPort, HttpMethod.Post, _token, JsonSerializer.Serialize(new { url = AddedURL })));
                Assert.IsTrue(held.Wait(Timeout));
                int secondPort = FindFreePort();
                Settings.ApiPort = secondPort;
                _ui.Invoke(() => _host.Apply());
                ApiServer starting = _host.ServerForTesting;
                Assert.AreEqual(LocalApiState.Starting, _host.Status.State, _host.Status.Text);

                _ui.Invoke(() => _host.Apply());
                Assert.AreSame(starting, _host.ServerForTesting, "A plain Apply replaced the running start.");
                string token = new ApiTokenStore(_folder).Generate();
                _ui.Invoke(() => _host.Apply(true));
                Assert.AreNotSame(starting, _host.ServerForTesting, "The start that was running was kept after the new token.");

                release.Set();
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, add.Result.Status, add.Result.Body);
                WaitForStart();
                Assert.AreEqual(LocalApiState.Listening, _host.Status.State, _host.Status.Text);
                Assert.AreEqual(HttpStatusCode.OK, Request(secondPort, HttpMethod.Get, token).Status);
            }
        }

        // A browser pairs with a code made as the dialog makes it (LocalApiPairing), sees the app's server name before
        // it saves its token, and the follow of the code reports the pairing; the token then adds a thread with its
        // Origin
        [TestMethod]
        public void ABrowserPairsWithTheDialogsCodeAndSeesTheAppsName() {
            int port = FindFreePort();
            EnableApi(port);
            LoadThreadList();
            LocalApiPairing pairing = new LocalApiPairing(_folder, () => DateTimeOffset.UtcNow);
            pairing.MakeCode();
            Assert.IsNull(pairing.Look());

            string origin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
            string clientNonce = ApiPairing.NewNonce();
            JsonElement hello = PostPairing(port, origin, new { step = "hello", clientNonce });
            string pairingId = hello.GetProperty("pairingId").GetString();
            string serverNonce = hello.GetProperty("serverNonce").GetString();
            string serverName = hello.GetProperty("serverName").GetString();
            byte[] key = ApiPairing.DeriveKey(pairing.Code.Code.Replace("-", ""), ApiPairing.FromBase64Url(hello.GetProperty("salt").GetString(), ApiPairing.SaltBytes));
            Assert.IsTrue(ProofMatches(ApiPairing.ServerHelloProof(key, port, origin, pairingId, clientNonce, serverNonce, serverName), hello), "P1 did not match the code.");
            string proof = ApiPairing.Base64Url(ApiPairing.ClientFinishProof(key, port, origin, pairingId, clientNonce, serverNonce));
            JsonElement finish = PostPairing(port, origin, new { step = "finish", pairingId, clientNonce, serverNonce, proof });
            string token = finish.GetProperty("token").GetString();

            Assert.AreEqual(LocalApiHost.ServerName(), serverName);
            StringAssert.StartsWith(serverName, "Chan Thread Watch " + General.Version + " on ");
            Assert.AreEqual(ApiPairingEnd.Paired, pairing.Look());
            Assert.AreEqual("chrome", pairing.PairedFamily);
            pairing.End();
            Assert.IsFalse(File.Exists(Path.Combine(_folder, ApiPairingFile.FileName)));
            Assert.AreEqual(HttpStatusCode.Created, Request(port, HttpMethod.Post, token, JsonSerializer.Serialize(new { url = AddedURL }), origin).Status);
        }

        private static bool ProofMatches(byte[] expected, JsonElement answer) {
            byte[] proof = ApiPairing.FromBase64Url(answer.GetProperty("proof").GetString(), ApiPairing.ProofBytes) ?? new byte[0];
            return CryptographicOperations.FixedTimeEquals(expected, proof);
        }

        // The answer's JSON; anything but 200 fails the test
        private static JsonElement PostPairing(int port, string origin, object body) {
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

        [TestMethod]
        public void TheServerNameIsTheAppVersionAndComputer() {
            Assert.AreEqual(ApiPolicy.NormalizeServerName("Chan Thread Watch " + General.Version + " on " + Environment.MachineName), LocalApiHost.ServerName());
        }

        [TestMethod]
        public void StartFailuresAreDescribedInPlainWords() {
            Assert.AreEqual("it could not listen on port 5000. The port may be reserved by Windows; choose another port.",
                LocalApiStatus.DescribeStartFailure(5000, new ApiStartException(ApiStartError.BindFailed, "x")));
            Assert.AreEqual("it was stopped while it started.", LocalApiStatus.DescribeStartFailure(5000, new ApiStartException(ApiStartError.Stopped, "x")));
            Assert.AreEqual("the program runs as the system's administrator account, and the local API does not run that way.",
                LocalApiStatus.DescribeStartFailure(5000, new ApiStartException(ApiStartError.Privileged, "x")));
            Assert.AreEqual("the local API could not start (see log.txt).", LocalApiStatus.DescribeStartFailure(5000, new IOException("C:\\secret\\path")));
        }

        // An add goes through the session on the UI thread, as any add: the events that make the window's row fire
        // there. The thread has the window's choices but no login or folder (D8), is guarded (its watcher never connects
        // to a local or private address), is marked in api-threads.txt with the next save, and the settings saved on
        // exit keep the API's settings.
        [TestMethod]
        public void AnApiAddGoesThroughTheSessionOnTheUiThreadAndIsGuardedAndSaved() {
            int port = FindFreePort();
            EnableApi(port);
            Settings.ApiAllowUnknownHosts = false;
            List<bool> createdOnUiThread = new List<bool>();
            _session.ThreadWatcherCreated += watcher => createdOnUiThread.Add(Thread.CurrentThread == _ui.Thread);
            LoadThreadList();

            ApiReply added = Request(port, HttpMethod.Post, _token, JsonSerializer.Serialize(new { url = AddedURL }));
            ApiReply withFolder = Request(port, HttpMethod.Post, _token, "{\"url\":\"" + AddedURL.Replace("300", "301") + "\",\"saveDir\":\"C:\\\\x\"}");

            Assert.AreEqual(HttpStatusCode.Created, added.Status, added.Body);
            Assert.AreEqual(HttpStatusCode.BadRequest, withFolder.Status, withFolder.Body);
            CollectionAssert.AreEqual(new[] { true }, createdOnUiThread);
            ThreadWatcher watcher = _session.ThreadWatchers.Single();
            Assert.AreEqual(AddedURL, watcher.PageURL);
            Assert.IsTrue(watcher.Guarded);
            Assert.IsTrue(watcher.OneTimeDownload);
            Assert.IsTrue(watcher.AutoFollow);
            Assert.AreEqual("from-window", watcher.Category);
            Assert.AreEqual(String.Empty, watcher.PageAuth);
            Assert.AreEqual(String.Empty, watcher.ImageAuth);
            Assert.IsTrue(_session.SaveThreadListPending);

            Assert.IsTrue(_ui.Invoke(() => _session.SaveThreadList()));
            Settings.Save();

            CollectionAssert.Contains(File.ReadAllLines(Path.Combine(_folder, Settings.ThreadsFileName)), AddedURL);
            CollectionAssert.AreEqual(new[] { "1", PageID(AddedURL) }, File.ReadAllLines(Path.Combine(_folder, Settings.ApiThreadsFileName)));
            string[] settings = File.ReadAllLines(SettingsPath);
            CollectionAssert.IsSubsetOf(new[] { "ApiEnabled=1", "ApiPort=" + port.ToString(CultureInfo.InvariantCulture), "ApiAllowUnknownHosts=0", "ApiThreadsFileWritten=1" }, settings);
            foreach (string path in Directory.GetFiles(_folder)) {
                Assert.IsFalse(File.ReadAllText(path).Contains(_token, StringComparison.Ordinal), "The token is in " + Path.GetFileName(path));
            }
            Assert.IsFalse(ReadLog().Contains(_token, StringComparison.Ordinal), "The token was logged.");
        }

        // The exit's stop closes the port and refuses the waiting add at once, without waiting for a request that holds
        // the stop (its lookup hangs): the window's first save is not delayed. The stop ends once the request is let go.
        [TestMethod]
        public void StopForExitReturnsAtOnceWhileARequestHoldsTheStop() {
            int port = FindFreePort();
            EnableApi(port);
            LocalApiHost.StopWait = TimeSpan.FromSeconds(20);
            LoadThreadList();
            using (ManualResetEventSlim held = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                _lookup = host => {
                    held.Set();
                    release.Wait(Timeout);
                    return new[] { IPAddress.Parse(ApiThreadAddress) };
                };
                Task<ApiReply> add = Task.Run(() => Request(port, HttpMethod.Post, _token, JsonSerializer.Serialize(new { url = AddedURL })));
                Assert.IsTrue(held.Wait(Timeout));

                Task stopped = _ui.Invoke(() => _host.StopForExit());

                bool stopWasPending = !stopped.IsCompleted;
                bool closedWhileHeld = WaitFor(() => !CanConnect(port));
                release.Set();
                Assert.IsTrue(stopWasPending, "StopForExit waited for the request.");
                Assert.IsTrue(closedWhileHeld, "The port stayed open while the request held the stop.");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, add.Result.Status, add.Result.Body);
                Assert.IsTrue(stopped.Wait(Timeout));
                Assert.AreEqual(0, _session.ThreadWatchers.Count);
            }
            // The port is free for another program, and a later Apply starts nothing
            TcpListener next = new TcpListener(IPAddress.Loopback, port);
            next.Start();
            next.Stop();
            _ui.Invoke(() => _host.Apply());
            Assert.IsNull(_host.ServerForTesting);
        }

        // The window stops the API on its UI thread and waits there for the stop after the saves. An add whose work is
        // posted to the UI thread, which is busy closing, must not hold the stop: it never runs and gets 503.
        [TestMethod]
        public void StopForExitOnTheUiThreadDoesNotWaitForWorkPostedThere() {
            int port = FindFreePort();
            EnableApi(port);
            LoadThreadList();
            using (ManualResetEventSlim posted = new ManualResetEventSlim(false)) {
                _post = work => {
                    _ui.Post(work);
                    posted.Set();
                };
                Task<ApiReply> add = null;
                bool stoppedOnUiThread = _ui.Invoke(() => {
                    add = Task.Run(() => Request(port, HttpMethod.Post, _token, JsonSerializer.Serialize(new { url = AddedURL })));
                    Assert.IsTrue(posted.Wait(Timeout), "The add's work was not posted.");
                    return _host.StopForExit().Wait(Timeout);
                });

                Assert.IsTrue(stoppedOnUiThread, "The stop waited for the UI thread.");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, add.Result.Status, add.Result.Body);
                Assert.AreEqual(0, _ui.Invoke(() => _session.ThreadWatchers.Count));
            }
        }

        // D8: the window's current choices, not the saved settings, and never a login or a folder. Each row has saved
        // settings that differ from the window's choices in every value, so a choice that is not taken fails.
        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public void ApiThreadsTakeTheWindowsChoicesWithoutALoginOrAFolder(bool savedOneTimeAndAutoFollow, bool chosenOneTimeAndAutoFollow) {
            Settings.OneTimeDownload = savedOneTimeAndAutoFollow;
            Settings.AutoFollow = savedOneTimeAndAutoFollow;
            Settings.CheckEvery = 10;
            NewThreadChoices choices = new NewThreadChoices {
                CheckIntervalSeconds = 120, OneTimeDownload = chosenOneTimeAndAutoFollow, AutoFollow = chosenOneTimeAndAutoFollow, Category = "cat"
            };

            ThreadInfo thread = choices.CreateApiThread(AddedURL);

            Assert.AreEqual(AddedURL, thread.URL);
            Assert.AreEqual(120, thread.CheckIntervalSeconds);
            Assert.AreEqual(chosenOneTimeAndAutoFollow, thread.OneTimeDownload);
            Assert.AreEqual(chosenOneTimeAndAutoFollow, thread.AutoFollow);
            Assert.AreEqual("cat", thread.Category);
            Assert.AreEqual(String.Empty, thread.PageAuth);
            Assert.AreEqual(String.Empty, thread.ImageAuth);
            Assert.AreEqual(String.Empty, thread.SaveDir);
            Assert.AreEqual(String.Empty, thread.Description);
            Assert.IsNull(thread.StopReason);
        }

        private void WriteSettingsAndLoad(params string[] lines) {
            File.WriteAllLines(SettingsPath, lines);
            Settings.Load();
            Settings.DownloadFolder = Path.Combine(_folder, "downloads");
            Settings.DownloadFolderIsRelative = false;
        }

        private static int CountOccurrences(string text, string value) {
            int count = 0;
            for (int i = text.IndexOf(value, StringComparison.Ordinal); i != -1; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
            return count;
        }

        private static string PageID(string url) {
            SiteHelper siteHelper = SiteHelpers.GetInstance(new Uri(url).Host);
            siteHelper.SetURL(url);
            return siteHelper.GetPageID();
        }

        private static string ReadLog() {
            using (FileStream stream = new FileStream(Path.Combine(TestAssemblySetup.LogFolder, Settings.LogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        private static bool WaitFor(Func<bool> condition) {
            DateTime end = DateTime.UtcNow + Timeout;
            while (!condition()) {
                if (DateTime.UtcNow > end) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        internal static int FindFreePort() {
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

        private static ApiReply Request(int port, HttpMethod method, string token, string json = null, string origin = null) {
            using (HttpClient client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout })
            using (HttpRequestMessage request = new HttpRequestMessage(method, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + ThreadsEndpoints.ThreadsPath)) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult()) {
                    return new ApiReply { Status = response.StatusCode, Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                }
            }
        }

        private static void DeleteFolder(string folder) {
            for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++) {
                try {
                    Directory.Delete(folder, true);
                }
                catch (IOException) {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) {
                    Thread.Sleep(100);
                }
            }
        }

        private sealed class ApiReply {
            public HttpStatusCode Status { get; set; }
            public string Body { get; set; }
        }
    }

    // A UI thread as the window's: an STA thread that runs a message loop, and a hidden control on it whose BeginInvoke
    // is the post delegate (as the window's) and whose Invoke runs work there and waits for it
    internal sealed class TestUiThread : IDisposable {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private Control _control;

        public TestUiThread() {
            _thread = new Thread(Run) { Name = "test UI thread", IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The test UI thread did not start.");
        }

        public Thread Thread {
            get { return _thread; }
        }

        private void Run() {
            _control = new Control();
            // The handle ties the control's BeginInvoke and Invoke to this thread
            _ = _control.Handle;
            _ready.Set();
            Application.Run();
            _control.Dispose();
        }

        public void Post(Action work) {
            _control.BeginInvoke(new MethodInvoker(work));
        }

        public void Invoke(Action work) {
            _control.Invoke(new MethodInvoker(work));
        }

        public T Invoke<T>(Func<T> work) {
            T result = default(T);
            Invoke(() => { result = work(); });
            return result;
        }

        public void Dispose() {
            Post(Application.ExitThread);
            _thread.Join(TimeSpan.FromSeconds(30));
            _ready.Dispose();
        }
    }
}
