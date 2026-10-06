using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JDP.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The tests set process-wide state (Settings, SSRFGuard's hooks, site registrations), so they run one at a time
[assembly: DoNotParallelize]

namespace JDP.Api.Tests {
    [TestClass]
    public static class ApiTestHost {
        // The folder of the app's log (Logger), which the log tests read
        public static string LogFolder { get; private set; }

        public static string LogPath {
            get { return Path.Combine(LogFolder, Settings.LogFileName); }
        }

        [AssemblyInitialize]
        public static void Initialize(TestContext context) {
            TestDefaultFolders.Redirect();
            // The log's path is taken once, by the first log: one fixed temporary folder, emptied here and reused by
            // every run, since the open log keeps it from being deleted on Windows until the test process ends
            LogFolder = Path.Combine(Path.GetTempPath(), "ctw-api-tests-log");
            DeleteFoldersOfEarlierVersions();
            Directory.CreateDirectory(LogFolder);
            File.WriteAllText(LogPath, String.Empty, Encoding.UTF8);
            Settings.SettingsDirectoryOverride = LogFolder;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Logger).TypeHandle);
            Settings.SettingsDirectoryOverride = null;
            Settings.UseExeDirectoryForSettings = true;
        }

        // Earlier versions of these tests made one log folder per run
        private static void DeleteFoldersOfEarlierVersions() {
            foreach (string folder in Directory.GetDirectories(Path.GetTempPath(), "ctw-api-tests-log-*")) {
                try {
                    Directory.Delete(folder, true);
                }
                catch (IOException) {
                }
                catch (UnauthorizedAccessException) {
                }
            }
        }

        [AssemblyCleanup]
        public static void Cleanup() {
            TestDefaultFolders.Delete();
            try {
                Directory.Delete(LogFolder, true);
            }
            catch (IOException) {
            }
            catch (UnauthorizedAccessException) {
            }
        }
    }

    // The owner thread of a host (the app's UI thread, ctw watch's owner thread): one thread that runs queued work in
    // order. Pause() holds it, to test a busy owner thread.
    public sealed class TestOwnerThread : IDisposable {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _running = new ManualResetEventSlim(true);

        public TestOwnerThread() {
            _thread = new Thread(Run) { Name = "API test owner", IsBackground = true };
            _thread.Start();
        }

        private void Run() {
            foreach (Action work in _queue.GetConsumingEnumerable()) {
                _running.Wait();
                work();
            }
        }

        public void Post(Action work) {
            _queue.Add(work);
        }

        public void Invoke(Action work) {
            if (Thread.CurrentThread == _thread) {
                work();
                return;
            }
            ExceptionDispatchInfo error = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false)) {
                Post(() => {
                    try { work(); }
                    catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                    finally { done.Set(); }
                });
                if (!done.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The owner thread did not run the work.");
            }
            error?.Throw();
        }

        public T Invoke<T>(Func<T> work) {
            T result = default(T);
            Invoke(() => { result = work(); });
            return result;
        }

        public void Pause() {
            _running.Reset();
        }

        public void Resume() {
            _running.Set();
        }

        public void Dispose() {
            Resume();
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    // Every log entry, at every level
    public sealed class CapturingLoggerProvider : ILoggerProvider {
        private readonly ConcurrentQueue<string> _entries = new ConcurrentQueue<string>();

        public IReadOnlyList<string> Entries {
            get { return _entries.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) {
            return new CapturingLogger(categoryName, _entries);
        }

        public void Dispose() {
        }

        private sealed class CapturingLogger : ILogger {
            private readonly string _category;
            private readonly ConcurrentQueue<string> _entries;

            public CapturingLogger(string category, ConcurrentQueue<string> entries) {
                _category = category;
                _entries = entries;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull {
                _entries.Enqueue("scope " + _category + ": " + state);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
                _entries.Enqueue(logLevel + " " + _category + " " + eventId + ": " + formatter(state, exception) + " " + state + " " + exception);
            }
        }
    }

    // A raw HTTP response read off a socket
    public sealed class RawResponse {
        public int Status { get; set; }
        public string Head { get; set; }
        public string Text { get; set; }

        public override string ToString() {
            return Text;
        }
    }

    // One real server per test: Kestrel on 127.0.0.1 with a free port, a real WatchSession on a test owner thread, a
    // temporary settings folder and download folder, a fresh token, and DNS answered by the test. The watchers of added
    // threads never reach the network: SSRFGuard's lookup fails for every host and no socket is connected.
    public abstract class ApiTestBase {
        protected const string ThreadUrl = "https://boards.4chan.org/wg/thread/100";
        protected const string PublicAddress = "203.0.113.10";

        private ApiServer _server;
        private readonly List<string> _registeredHosts = new List<string>();

        protected string Folder { get; private set; }
        protected TestOwnerThread Owner { get; private set; }
        internal WatchSession Session { get; private set; }
        internal OwnerThreadDispatcher Dispatcher { get; private set; }
        internal ApiPolicy Policy { get; private set; }
        internal ApiTokenStore Tokens { get; private set; }
        protected string Token { get; set; }
        protected int Port { get; private set; }
        protected HttpClient Client { get; private set; }
        protected CapturingLoggerProvider Log { get; private set; }

        // Makes the thread to add; read on every add, so a test can change it after the server started
        internal Func<string, ThreadInfo> NewThreadFactory { get; set; } = url => NewThread.Create(url, null, null);

        // The test's DNS: host name to addresses; a host that is not listed does not resolve
        protected Dictionary<string, IPAddress[]> TestDns { get; } = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
        protected List<string> ResolvedHosts { get; } = new List<string>();

        internal ApiServer Server {
            get { return _server; }
        }

        protected string ThreadListPath {
            get { return Path.Combine(Folder, Settings.ThreadsFileName); }
        }

        protected string SettingsPath {
            get { return Path.Combine(Folder, Settings.SettingsFileName); }
        }

        [TestInitialize]
        public void StartServer() {
            Folder = Path.Combine(Path.GetTempPath(), "ctw-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Folder, "downloads"));
            Settings.Load(Path.Combine(Folder, "missing-settings.txt"));
            Settings.DownloadFolder = Path.Combine(Folder, "downloads");
            Settings.DownloadFolderIsRelative = false;
            SSRFGuard.ResolveHost = (host, token) => throw new SocketException((int)SocketError.HostNotFound);
            SSRFGuard.BeforeConnect = addresses => throw new InvalidOperationException("The API tests never connect.");
            TestDns["boards.4chan.org"] = new[] { IPAddress.Parse(PublicAddress) };
            Owner = new TestOwnerThread();
            Session = new WatchSession(Owner.Invoke, Owner.Post, Folder);
            Dispatcher = new OwnerThreadDispatcher(Owner.Post);
            Policy = new ApiPolicy { ResolveHost = ResolveForTest, GetFreeSpace = () => null };
            ConfigurePolicy(Policy);
            Tokens = new ApiTokenStore(Folder);
            Token = Tokens.Generate();
            Log = new CapturingLoggerProvider();
            StartNewServer();
        }

        // A test class may change the limits before the server starts
        internal virtual void ConfigurePolicy(ApiPolicy policy) {
        }

        protected void StartNewServer() {
            _server = new ApiServer(new ApiThreadService(Session, Dispatcher, url => NewThreadFactory(url), Policy), Tokens) { LoggerProviderForTesting = Log };
            Port = _server.StartAsync(0).GetAwaiter().GetResult();
            Client?.Dispose();
            Client = CreateClient(Port);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        protected static HttpClient CreateClient(int port) {
            HttpClient client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) {
                BaseAddress = new Uri("http://127.0.0.1:" + port),
                Timeout = TimeSpan.FromSeconds(30)
            };
            return client;
        }

        private Task<IPAddress[]> ResolveForTest(string host, CancellationToken cancellationToken) {
            lock (ResolvedHosts) {
                ResolvedHosts.Add(host);
            }
            IPAddress[] addresses;
            if (TestDns.TryGetValue(host, out addresses)) return Task.FromResult(addresses);
            throw new SocketException((int)SocketError.HostNotFound);
        }

        protected void RegisterHost(string host, Type helperType) {
            _registeredHosts.Add(host);
            SiteHelpers.RegisterHostForTesting(host, helperType);
        }

        [TestCleanup]
        public void StopServer() {
            try {
                _server?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Client?.Dispose();
                StopWatchers();
                Owner?.Dispose();
            }
            finally {
                foreach (string host in _registeredHosts) {
                    SiteHelpers.UnregisterHostForTesting(host);
                }
                SSRFGuard.ResolveHost = Dns.GetHostAddressesAsync;
                SSRFGuard.BeforeConnect = addresses => { };
                OwnerOnlyFile.IsRootOnUnix = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
                OwnerOnlyFile.NewFileCheckForTesting = null;
                Settings.Load(Path.Combine(Folder, "missing-settings.txt"));
                DeleteFolder();
            }
        }

        private void StopWatchers() {
            if (Session == null) return;
            List<ThreadWatcher> watchers = Session.ThreadWatchers;
            foreach (ThreadWatcher watcher in watchers) {
                watcher.Stop(StopReason.Exiting);
            }
            foreach (ThreadWatcher watcher in watchers) {
                watcher.WaitUntilStopped(10000);
                watcher.WaitReparse(10000);
            }
        }

        private void DeleteFolder() {
            for (int attempt = 0; attempt < 10 && Directory.Exists(Folder); attempt++) {
                try {
                    Directory.Delete(Folder, true);
                }
                catch (IOException) {
                    Thread.Sleep(100);
                }
            }
        }

        // Requests

        protected HttpResponseMessage Send(HttpMethod method, string path, string json = null, Action<HttpRequestMessage> configure = null) {
            using (HttpRequestMessage request = new HttpRequestMessage(method, path)) {
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                configure?.Invoke(request);
                return Client.SendAsync(request).GetAwaiter().GetResult();
            }
        }

        protected HttpResponseMessage Get(string path, Action<HttpRequestMessage> configure = null) {
            return Send(HttpMethod.Get, path, null, configure);
        }

        protected HttpResponseMessage PostJson(string json, Action<HttpRequestMessage> configure = null) {
            return Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, json, configure);
        }

        protected HttpResponseMessage AddThread(string url) {
            return PostJson(JsonSerializer.Serialize(new { url }));
        }

        protected static string Body(HttpResponseMessage response) {
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        protected static JsonElement Json(HttpResponseMessage response) {
            using (JsonDocument document = JsonDocument.Parse(Body(response))) {
                return document.RootElement.Clone();
            }
        }

        // A problem+json error with the status and code, and the headers every response has
        protected static void AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code) {
            string body = Body(response);
            Assert.AreEqual(status, response.StatusCode, body);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, body);
            JsonElement problem = JsonDocument.Parse(body).RootElement;
            Assert.AreEqual(code, problem.GetProperty("code").GetString(), body);
            Assert.AreEqual((int)status, problem.GetProperty("status").GetInt32(), body);
            AssertCommonHeaders(response);
        }

        protected static void AssertCommonHeaders(HttpResponseMessage response) {
            Assert.IsTrue(response.Headers.CacheControl?.NoStore == true, "Cache-Control: no-store");
            Assert.AreEqual("nosniff", String.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
            Assert.IsFalse(response.Headers.Contains("Server"), "Server header");
            AssertNoCors(response);
        }

        protected static void AssertNoCors(HttpResponseMessage response) {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers)) {
                Assert.IsFalse(header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase), header.Key);
            }
        }

        // Sends the bytes as they are on a new connection and reads the whole response (the server closes it)
        protected RawResponse SendRaw(string request) {
            using (TcpClient client = new TcpClient()) {
                client.Connect(IPAddress.Loopback, Port);
                client.ReceiveTimeout = 15000;
                NetworkStream stream = client.GetStream();
                byte[] bytes = Encoding.ASCII.GetBytes(request);
                stream.Write(bytes, 0, bytes.Length);
                string text = new StreamReader(stream, Encoding.ASCII).ReadToEnd();
                int status = Int32.Parse(text.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
                int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                return new RawResponse { Status = status, Head = headEnd >= 0 ? text.Substring(0, headEnd) : text, Text = text };
            }
        }

        protected string RawGet(string target, string host, string extraHeaders = "", string version = "HTTP/1.1") {
            string hostLine = host != null ? "Host: " + host + "\r\n" : "";
            return "GET " + target + " " + version + "\r\n" + hostLine + "Authorization: Bearer " + Token + "\r\n" + extraHeaders + "Connection: close\r\n\r\n";
        }

        protected int ThreadCount() {
            return Owner.Invoke(() => Session.ThreadWatchers.Count);
        }

        protected static string FileHash(string path) {
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
        }

        // The thread list and settings files, written as a host would have them, to check that a request left them as
        // they were
        protected void WriteSettingsFiles() {
            File.WriteAllLines(ThreadListPath, new[] { ThreadListFile.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            File.WriteAllLines(SettingsPath, new[] { "DownloadFolder=" + Path.Combine(Folder, "downloads"), "DownloadFolderIsRelative=0" });
        }
    }
}
