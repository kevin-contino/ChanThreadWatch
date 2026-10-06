using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    [TestClass]
    public class ListThreadsTests : ApiTestBase {
        private static readonly string[] ThreadMembers = { "id", "url", "description", "category", "state", "stopReason", "addedOn", "addedFrom" };

        [TestMethod]
        public void List_EmptySessionGivesAnEmptyList() {
            HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("{\"threads\":[]}", Body(response));
        }

        // Only the projection: no folder, login, interval or error text; the URL without its login
        [TestMethod]
        public void List_ShowsTheProjectionOnly() {
            ThreadInfo thread = NewThread.Create("https://user:secret@boards.4chan.org/wg/thread/5", "Desc", "Cat");
            thread.PageAuth = "page:login";
            thread.SaveDir = Path.Combine(Folder, "downloads", "secret-folder");
            thread.StopReason = StopReason.UserRequest;
            Owner.Invoke(() => Session.AddThread(thread));
            string body = Body(Get(ThreadsEndpoints.ThreadsPath));
            JsonElement item = JsonDocument.Parse(body).RootElement.GetProperty("threads")[0];
            CollectionAssert.AreEqual(ThreadMembers, item.EnumerateObject().Select(member => member.Name).ToArray());
            Assert.AreEqual("4chan/wg/5", item.GetProperty("id").GetString());
            Assert.AreEqual("https://boards.4chan.org/wg/thread/5", item.GetProperty("url").GetString());
            Assert.AreEqual("Desc", item.GetProperty("description").GetString());
            Assert.AreEqual("Cat", item.GetProperty("category").GetString());
            Assert.AreEqual("stopped", item.GetProperty("state").GetString());
            Assert.AreEqual("userRequest", item.GetProperty("stopReason").GetString());
            Assert.AreEqual(JsonValueKind.Null, item.GetProperty("addedFrom").ValueKind);
            DateTimeOffset addedOn = DateTimeOffset.Parse(item.GetProperty("addedOn").GetString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(Math.Abs((DateTimeOffset.Now - addedOn).TotalMinutes) < 5);
            foreach (string secret in new[] { "secret", "page:login", "secret-folder", Folder }) {
                Assert.IsFalse(body.Contains(secret.Replace("\\", "\\\\"), StringComparison.Ordinal), secret);
            }
        }

        [TestMethod]
        public void List_StatesAndStopReasons() {
            Owner.Invoke(() => {
                AddStopped("https://boards.4chan.org/wg/thread/1", StopReason.PageNotFound);
                AddStopped("https://boards.4chan.org/wg/thread/2", StopReason.IOError);
                AddStopped("https://boards.4chan.org/wg/thread/3", StopReason.DownloadComplete);
                AddStopped("https://boards.4chan.org/wg/thread/5", StopReason.Exiting);
                AddStopped("https://boards.4chan.org/wg/thread/6", StopReason.Other);
                AddStopped("https://boards.4chan.org/wg/thread/7", StopReason.UserRequest);
            });
            Dictionary<string, JsonElement> byId = ListById();
            AssertState(byId["4chan/wg/1"], "notFound", "pageNotFound");
            AssertState(byId["4chan/wg/2"], "stopped", "ioError");
            AssertState(byId["4chan/wg/3"], "stopped", "downloadComplete");
            AssertState(byId["4chan/wg/5"], "stopped", "exiting");
            AssertState(byId["4chan/wg/6"], "stopped", "other");
            AssertState(byId["4chan/wg/7"], "stopped", "userRequest");
        }

        // Running while its check runs (held here at the watcher's DNS lookup), waiting once the check is over
        [TestMethod]
        public void List_RunningThenWaiting() {
            using (ManualResetEventSlim lookupStarted = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                SSRFGuard.ResolveHost = (host, token) => {
                    lookupStarted.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    throw new SocketException((int)SocketError.HostNotFound);
                };
                Assert.AreEqual(HttpStatusCode.Created, AddThread("https://boards.4chan.org/wg/thread/4").StatusCode);
                Assert.IsTrue(lookupStarted.Wait(TimeSpan.FromSeconds(30)));
                AssertState(ListById()["4chan/wg/4"], "running", null);
                release.Set();
                ThreadWatcher watcher = Owner.Invoke(() => Session.ThreadWatchers.Single());
                Assert.IsTrue(SpinWait.SpinUntil(() => watcher.IsWaiting, TimeSpan.FromSeconds(30)));
                AssertState(ListById()["4chan/wg/4"], "waiting", null);
            }
        }

        private Dictionary<string, JsonElement> ListById() {
            JsonElement threads = Json(Get(ThreadsEndpoints.ThreadsPath)).GetProperty("threads");
            return threads.EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString());
        }

        private void AddStopped(string url, StopReason reason) {
            ThreadInfo thread = NewThread.Create(url, null, null);
            thread.StopReason = reason;
            Session.AddThread(thread);
        }

        private static void AssertState(JsonElement item, string state, string stopReason) {
            Assert.AreEqual(state, item.GetProperty("state").GetString());
            if (stopReason == null) Assert.AreEqual(JsonValueKind.Null, item.GetProperty("stopReason").ValueKind);
            else Assert.AreEqual(stopReason, item.GetProperty("stopReason").GetString());
        }
    }

    // The owner thread is busy or gone, and the server stops
    [TestClass]
    public class LifetimeTests : ApiTestBase {
        internal override void ConfigurePolicy(ApiPolicy policy) {
            policy.OwnerThreadTimeout = TimeSpan.FromMilliseconds(500);
        }

        [TestMethod]
        public void Busy_OwnerThreadGives503AndTheAddNeverRuns() {
            Owner.Pause();
            Stopwatch elapsed = Stopwatch.StartNew();
            AssertProblem(Get(ThreadsEndpoints.ThreadsPath), HttpStatusCode.ServiceUnavailable, "unavailable");
            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.ServiceUnavailable, "unavailable");
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(10), elapsed.Elapsed.ToString());
            Owner.Resume();
            Assert.AreEqual(0, ThreadCount());
            Assert.IsFalse(Owner.Invoke(() => Session.SaveThreadListPending));
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        // A request waiting for the owner thread gets 503 as soon as the server stops, and the stop ends in time
        [TestMethod]
        public void Stop_PendingRequestGets503QuicklyAndTheAddNeverRuns() {
            Policy.OwnerThreadTimeout = TimeSpan.FromSeconds(30);
            Owner.Pause();
            Task<HttpResponseMessage> list = Client.GetAsync(ThreadsEndpoints.ThreadsPath);
            Task<HttpResponseMessage> add = Client.PostAsync(ThreadsEndpoints.ThreadsPath, new StringContent("{\"url\":\"" + ThreadUrl + "\"}", Encoding.UTF8, "application/json"));
            Thread.Sleep(500);
            Assert.IsFalse(list.IsCompleted);
            Stopwatch elapsed = Stopwatch.StartNew();
            Server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
            Assert.IsTrue(Task.WaitAll(new Task[] { list, add }, TimeSpan.FromSeconds(5)));
            AssertProblem(list.Result, HttpStatusCode.ServiceUnavailable, "unavailable");
            AssertProblem(add.Result, HttpStatusCode.ServiceUnavailable, "unavailable");
            Owner.Resume();
            Assert.AreEqual(0, ThreadCount());
            Assert.ThrowsExactly<HttpRequestException>(() => Client.GetAsync(ThreadsEndpoints.ThreadsPath).GetAwaiter().GetResult());
        }

        // The owner's work started before the request timed out: it finishes there, so the request keeps its result
        [TestMethod]
        public void Busy_WorkThatStartedBeforeTheTimeoutStillAdds() {
            NewThreadFactory = url => {
                Thread.Sleep(1500);
                return NewThread.Create(url, null, null);
            };
            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            Assert.AreEqual(1, ThreadCount());
        }

        // The app's UI thread stops the server while a request waits for that same thread: the stop needs nothing from
        // it (no continuation goes back to its SynchronizationContext) and ends well within its timeout
        [TestMethod]
        public void Stop_FromTheOwnerThreadWithASynchronizationContextEndsInTime() {
            Policy.OwnerThreadTimeout = TimeSpan.FromSeconds(30);
            TimeSpan elapsed = TimeSpan.Zero;
            Task<HttpResponseMessage> list = null;
            Owner.Invoke(() => {
                SynchronizationContext.SetSynchronizationContext(new OwnerSynchronizationContext(Owner));
                try {
                    list = Client.GetAsync(ThreadsEndpoints.ThreadsPath);
                    Thread.Sleep(500);
                    Stopwatch stop = Stopwatch.StartNew();
                    Server.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    elapsed = stop.Elapsed;
                }
                finally {
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            });
            Assert.IsTrue(elapsed < TimeSpan.FromSeconds(3), elapsed.ToString());
            Assert.IsTrue(list.Wait(TimeSpan.FromSeconds(5)));
            AssertProblem(list.Result, HttpStatusCode.ServiceUnavailable, "unavailable");
        }

        // Close never runs a pending request's code on the thread that calls it
        [TestMethod]
        public void Dispatcher_CloseNeverRunsRequestCodeOnTheCaller() {
            OwnerThreadDispatcher dispatcher = new OwnerThreadDispatcher(Owner.Post);
            Owner.Pause();
            Task<int> pending = dispatcher.RunAsync(() => 1, TimeSpan.FromSeconds(30));
            int ranOn = 0;
            Task continuation = pending.ContinueWith(task => ranOn = Environment.CurrentManagedThreadId, TaskContinuationOptions.ExecuteSynchronously);
            dispatcher.Close();
            Assert.IsTrue(continuation.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, ranOn);
            Owner.Resume();
        }

        // Posts to the owner thread, as WindowsFormsSynchronizationContext does
        private sealed class OwnerSynchronizationContext : SynchronizationContext {
            private readonly TestOwnerThread _owner;

            public OwnerSynchronizationContext(TestOwnerThread owner) {
                _owner = owner;
            }

            public override void Post(SendOrPostCallback d, object state) {
                _owner.Post(() => d(state));
            }

            public override void Send(SendOrPostCallback d, object state) {
                throw new InvalidOperationException("Send would block the owner thread.");
            }
        }

        [TestMethod]
        public void Dispatcher_AfterCloseFailsAtOnce() {
            OwnerThreadDispatcher dispatcher = new OwnerThreadDispatcher(Owner.Post);
            Assert.AreEqual(3, dispatcher.RunAsync(() => 3, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            dispatcher.Close();
            Assert.IsTrue(dispatcher.IsClosed);
            Assert.ThrowsExactly<ApiUnavailableException>(() => dispatcher.RunAsync(() => 3, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }

        [TestMethod]
        public void Dispatcher_WorkQueuedBeforeCloseNeverRuns() {
            OwnerThreadDispatcher dispatcher = new OwnerThreadDispatcher(Owner.Post);
            int runs = 0;
            Owner.Pause();
            Task<int> pending = dispatcher.RunAsync(() => Interlocked.Increment(ref runs), TimeSpan.FromSeconds(30));
            dispatcher.Close();
            Assert.ThrowsExactly<ApiUnavailableException>(() => pending.GetAwaiter().GetResult());
            Owner.Resume();
            Owner.Invoke(() => { });
            Assert.AreEqual(0, runs);
        }

        // The window is gone: its BeginInvoke throws
        [TestMethod]
        public void Dispatcher_FailingPostIsUnavailable() {
            OwnerThreadDispatcher dispatcher = new OwnerThreadDispatcher(work => throw new InvalidOperationException("no window"));
            Assert.ThrowsExactly<ApiUnavailableException>(() => dispatcher.RunAsync(() => 1, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }

        [TestMethod]
        public void Dispatcher_WorkExceptionReachesTheCaller() {
            OwnerThreadDispatcher dispatcher = new OwnerThreadDispatcher(Owner.Post);
            Assert.ThrowsExactly<FormatException>(() => dispatcher.RunAsync<int>(() => throw new FormatException(), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
    }

    // Where the server listens: 127.0.0.1 and the port given, never an address from the environment or a file
    [TestClass]
    public class BindTests : ApiTestBase {
        [TestMethod]
        public void Bind_OnlyIPv4Loopback() {
            if (Socket.OSSupportsIPv6) {
                using (TcpClient client = new TcpClient(AddressFamily.InterNetworkV6)) {
                    Assert.ThrowsExactly<SocketException>(() => client.Connect(IPAddress.IPv6Loopback, Port));
                }
            }
            IEnumerable<IPAddress> addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
            foreach (IPAddress address in addresses) {
                using (TcpClient client = new TcpClient()) {
                    Assert.ThrowsExactly<SocketException>(() => client.Connect(address, Port), address.ToString());
                }
            }
        }

        // The host (the app, ctw watch) handles Ctrl+C and the other stop signals itself. The default lifetime
        // (ConsoleLifetime) would register its own handlers for SIGINT, SIGQUIT and SIGTERM, which cancel every such
        // signal while the server runs, so ctw watch's second Ctrl+C would no longer end the process.
        [TestMethod]
        public void Host_HandlesNoSignals() {
            IHostLifetime lifetime = Server.ServicesForTesting.GetRequiredService<IHostLifetime>();

            Assert.IsNotInstanceOfType<ConsoleLifetime>(lifetime, lifetime.GetType().FullName);
            Assert.AreEqual("JDP.Api.ApiHostLifetime", lifetime.GetType().FullName);
        }

        [TestMethod]
        public void Bind_IgnoresEnvironmentAndAppSettings() {
            int envPort = FreePort();
            int filePort = FreePort();
            string currentFolder = Environment.CurrentDirectory;
            string binAppSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            Dictionary<string, string> variables = new Dictionary<string, string> {
                ["ASPNETCORE_URLS"] = "http://0.0.0.0:" + envPort,
                ["DOTNET_URLS"] = "http://0.0.0.0:" + envPort,
                ["ASPNETCORE_HTTP_PORTS"] = envPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = "Missing.Startup.Assembly",
                // These make the URLs above win over the code's Listen call in a default host
                ["ASPNETCORE_PREFERHOSTINGURLS"] = "true",
                ["DOTNET_PREFERHOSTINGURLS"] = "true"
            };
            Dictionary<string, string> saved = variables.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try {
                foreach (KeyValuePair<string, string> variable in variables) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                string appSettings = "{\"Kestrel\":{\"Endpoints\":{\"Http\":{\"Url\":\"http://0.0.0.0:" + filePort + "\"}}},\"urls\":\"http://0.0.0.0:" + filePort + "\"}";
                File.WriteAllText(Path.Combine(Folder, "appsettings.json"), appSettings);
                // Next to the test assembly too (the content root); deleted below
                Assert.IsFalse(File.Exists(binAppSettings), "an appsettings.json is already next to the tests");
                File.WriteAllText(binAppSettings, appSettings);
                Environment.CurrentDirectory = Folder;
                StartAnotherServerAndList();
                AssertNotListening(envPort);
                AssertNotListening(filePort);
            }
            finally {
                File.Delete(binAppSettings);
                Environment.CurrentDirectory = currentFolder;
                foreach (KeyValuePair<string, string> variable in saved) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
        }

        [TestMethod]
        public void Bind_PortInUseIsATypedErrorWithNoOtherPort() {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try {
                int busyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                ApiServer server = NewServer();
                ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(busyPort).GetAwaiter().GetResult());
                Assert.AreEqual(ApiStartError.PortInUse, error.Error);
                StringAssert.Contains(error.Message, busyPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.AreEqual(0, server.BoundPort);
                // Once the port is free, the same server starts
                listener.Stop();
                Assert.AreEqual(busyPort, server.StartAsync(busyPort).GetAwaiter().GetResult());
                server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                AssertNotListening(busyPort);
            }
            finally {
                listener.Stop();
            }
        }

        [TestMethod]
        public void Start_TwiceIsRefusedAndBadPortsAreRefused() {
            Assert.ThrowsExactly<InvalidOperationException>(() => Server.StartAsync(0).GetAwaiter().GetResult());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewServer().StartAsync(-1).GetAwaiter().GetResult());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewServer().StartAsync(65536).GetAwaiter().GetResult());
            // A stopped server's dispatcher stays closed, so it never starts again (it would answer every request 503)
            Server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.ThrowsExactly<InvalidOperationException>(() => Server.StartAsync(0).GetAwaiter().GetResult());
        }

        // A stop while the server starts wins: the stop waits for the start, returns with the port already free, and
        // the start throws Stopped
        [TestMethod]
        public void Start_StopDuringStartReturnsWithThePortFree() {
            int port = FreePort();
            ApiServer server = NewServer();
            using (ManualResetEventSlim listening = new ManualResetEventSlim(false))
            using (SemaphoreSlim release = new SemaphoreSlim(0)) {
                server.ListeningForTesting = async () => {
                    listening.Set();
                    await release.WaitAsync();
                };
                Task<int> start = server.StartAsync(port);
                Assert.IsTrue(listening.Wait(TimeSpan.FromSeconds(30)));
                Task stop = server.StopAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(stop.Wait(300), "the stop returned while the start still had the port");
                release.Release();
                Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(30)));
                AssertNotListening(port);
                Assert.AreEqual(0, server.BoundPort);
                ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => start.GetAwaiter().GetResult());
                Assert.AreEqual(ApiStartError.Stopped, error.Error);
            }
        }

        // The API turned off and on at once (a new server on the same port, as the hosts do) gets the port
        [TestMethod]
        public void Start_QuickOffAndOnGetsThePort() {
            int port = FreePort();
            for (int i = 0; i < 5; i++) {
                ApiServer server = NewServer();
                Assert.AreEqual(port, server.StartAsync(port).GetAwaiter().GetResult());
                server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            AssertNotListening(port);
        }

        // A failure after Kestrel listens (here the test hook) leaves nothing listening, is typed, and the same server
        // can start again
        [TestMethod]
        public void Start_FailureAfterListeningIsTypedAndReleasesThePort() {
            int port = FreePort();
            ApiServer server = NewServer();
            server.ListeningForTesting = () => throw new InvalidOperationException("setup failed");
            ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(port).GetAwaiter().GetResult());
            Assert.AreEqual(ApiStartError.BindFailed, error.Error);
            Assert.AreEqual(0, server.BoundPort);
            AssertNotListening(port);
            server.ListeningForTesting = null;
            Assert.AreEqual(port, server.StartAsync(port).GetAwaiter().GetResult());
            server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void Stop_BadTimeoutIsRefusedAndRepeatedStopsAreSafe() {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Server.StopAsync(TimeSpan.FromSeconds(-2)));
            Task.WaitAll(Server.StopAsync(TimeSpan.FromSeconds(5)), Server.StopAsync(TimeSpan.FromSeconds(5)));
            Server.StopAsync(TimeSpan.Zero).GetAwaiter().GetResult();
            Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AssertNotListening(Port);
        }

        // Root on Linux or macOS: the token file's mode proves nothing, so the API does not start
        [TestMethod]
        public void Start_AsRootOnUnixIsRefused() {
            OwnerOnlyFile.IsRootOnUnix = () => true;
            ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => NewServer().StartAsync(0).GetAwaiter().GetResult());
            Assert.AreEqual(ApiStartError.Privileged, error.Error);
            if (!OperatingSystem.IsWindows()) Assert.IsFalse(Tokens.Verify(Token));
        }

        [TestMethod]
        public void Start_BindFailuresAreTyped() {
            Assert.AreEqual(ApiStartError.PortInUse, ApiServer.ToStartException(new IOException("bind", new Microsoft.AspNetCore.Connections.AddressInUseException("in use")), 1).Error);
            Assert.AreEqual(ApiStartError.BindFailed, ApiServer.ToStartException(new SocketException((int)SocketError.AccessDenied), 1).Error);
            Assert.AreEqual(ApiStartError.BindFailed, ApiServer.ToStartException(new IOException("bind", new SocketException((int)SocketError.AccessDenied)), 1).Error);
        }

        // A port under 1024 needs root on Linux: BindFailed, nothing listening, BoundPort 0
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void Start_PrivilegedPortIsBindFailed() {
            if (Environment.UserName == "root") Assert.Inconclusive("Runs as root, which may bind any port.");
            ApiServer server = NewServer();
            ApiStartException error = Assert.ThrowsExactly<ApiStartException>(() => server.StartAsync(1).GetAwaiter().GetResult());
            Assert.AreEqual(ApiStartError.BindFailed, error.Error);
            Assert.AreEqual(0, server.BoundPort);
        }

        private ApiServer NewServer() {
            return new ApiServer(new ApiThreadService(Session, new OwnerThreadDispatcher(Owner.Post), url => NewThread.Create(url, null, null), Policy), Tokens);
        }

        private void StartAnotherServerAndList() {
            ApiServer server = NewServer();
            int port = server.StartAsync(0).GetAwaiter().GetResult();
            try {
                using (HttpClient client = CreateClient(port)) {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                    Assert.AreEqual(HttpStatusCode.OK, client.GetAsync(ThreadsEndpoints.ThreadsPath).GetAwaiter().GetResult().StatusCode);
                }
            }
            finally {
                server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }

        private static int FreePort() {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void AssertNotListening(int port) {
            foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }) {
                using (TcpClient client = new TcpClient(address.AddressFamily)) {
                    Assert.ThrowsExactly<SocketException>(() => client.Connect(address, port), address + ":" + port);
                }
            }
        }
    }
}
