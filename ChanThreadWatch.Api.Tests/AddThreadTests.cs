using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // POST /api/v1/threads: the body (url only, G11), the URL rules (G12, security item 9), and the add itself
    [TestClass]
    public class AddThreadTests : ApiTestBase {
        [TestMethod]
        public void Add_KnownSiteIsAddedWithTheDefaults() {
            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            JsonElement thread = Json(response);
            Assert.AreEqual("4chan/wg/100", thread.GetProperty("id").GetString());
            Assert.AreEqual(ThreadUrl, thread.GetProperty("url").GetString());
            Assert.AreEqual(1, ThreadCount());
            Assert.IsTrue(Owner.Invoke(() => Session.SaveThreadListPending));
            ThreadWatcher watcher = Owner.Invoke(() => Session.ThreadWatchers[0]);
            Assert.AreEqual(ThreadUrl, watcher.PageURL);
            Assert.AreEqual(String.Empty, watcher.PageAuth);
            Assert.AreEqual(String.Empty, watcher.ImageAuth);
            // No description is given; the watcher names the thread itself once its first check starts, as in the app
            CollectionAssert.Contains(new[] { "", "4chan_wg_100" }, watcher.Description);
            CollectionAssert.Contains(ResolvedHosts, "boards.4chan.org");
        }

        [TestMethod]
        public void Add_CharsetUtf8IsAccepted() {
            HttpResponseMessage response = Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Content = new StringContent("{\"url\":\"" + ThreadUrl + "\"}", Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=UTF-8");
            });
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
        }

        // G11 and security item 10: no folder or any other member, whatever its value; nothing is added or written
        [TestMethod]
        public void Body_FolderAndOtherMembersAreRefused() {
            // The hashes can only change if the API writes these files; SourceTests.Library_NeverWritesTheThreadListOrSettings
            // checks that it never references their writers. The thread count is the check that matters here.
            WriteSettingsFiles();
            string threadsHash = FileHash(ThreadListPath);
            string settingsHash = FileHash(SettingsPath);
            string[] values = { "../../etc", "C:\\Windows", "/etc", "\\\\host\\share", "\\\\?\\C:\\", "a\nb", "" };
            string[] members = { "folder", "SaveDir", "saveDir", "savedir", "downloadFolder", "DownloadFolder", "path", "description", "category", "pageAuth", "auth" };
            foreach (string member in members) {
                foreach (string value in values) {
                    string body = "{\"url\":" + JsonSerializer.Serialize(ThreadUrl) + "," + JsonSerializer.Serialize(member) + ":" + JsonSerializer.Serialize(value) + "}";
                    AssertProblem(PostJson(body), HttpStatusCode.BadRequest, "invalid_body");
                }
            }
            Assert.AreEqual(0, ThreadCount());
            Assert.AreEqual(threadsHash, FileHash(ThreadListPath));
            Assert.AreEqual(settingsHash, FileHash(SettingsPath));
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(Folder, "downloads")).Length);
        }

        [TestMethod]
        public void Body_ShapeOtherThanOneUrlStringIsRefused() {
            string url = JsonSerializer.Serialize(ThreadUrl);
            string[] bodies = {
                "", " ", "null", "[]", "[" + url + "]", url, "1", "true", "{}", "{\"url\":null}", "{\"url\":1}", "{\"url\":true}",
                "{\"url\":[" + url + "]}", "{\"url\":{\"url\":" + url + "}}", "{\"URL\":" + url + "}", "{\"Url\":" + url + "}",
                "{\"url\":" + url + ",\"url\":" + url + "}", "{\"url\":" + url + ",\"URL\":" + url + "}", "{\"url\":" + url + "} x",
                "{\"url\":" + url + "}{}", "{\"url\":" + url + ",}", "{\"url\":" + url + " /* c */}", "{'url':" + url + "}",
                "{\"url\":" + url + ",\"nested\":{\"folder\":\"/etc\"}}", "{\"url\":\"" + ThreadUrl
            };
            foreach (string body in bodies) {
                AssertProblem(PostJson(body), HttpStatusCode.BadRequest, "invalid_body");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Body_InvalidUtf8IsRefused() {
            HttpResponseMessage response = Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Content = new ByteArrayContent(new byte[] { 0x7B, 0x22, 0x75, 0x72, 0x6C, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D });
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            });
            AssertProblem(response, HttpStatusCode.BadRequest, "invalid_body");
        }

        [TestMethod]
        public void Body_OtherContentTypesAre415() {
            string body = "{\"url\":\"" + ThreadUrl + "\"}";
            foreach (string contentType in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x", "application/json; charset=utf-16", "application/jsonx", "application/problem+json", "text/json" }) {
                HttpResponseMessage response = Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, null, request => {
                    request.Content = new StringContent(body, Encoding.UTF8);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                });
                AssertProblem(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            }
            HttpResponseMessage none = Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            });
            AssertProblem(none, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Body_Over4KiBIs413() {
            string padded = "{\"url\":\"" + ThreadUrl + "\"" + new string(' ', 4096) + "}";
            // The server answers without reading the body and closes the connection, so the client waits for 100-continue and does not reuse it
            AssertProblem(PostJson(padded, request => { request.Headers.ExpectContinue = true; request.Headers.ConnectionClose = true; }), HttpStatusCode.RequestEntityTooLarge, "body_too_large");
            // Chunked, with no Content-Length
            HttpResponseMessage chunked = Send(HttpMethod.Post, ThreadsEndpoints.ThreadsPath, null, request => {
                request.Headers.ConnectionClose = true;
                request.Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(padded)));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            });
            AssertProblem(chunked, HttpStatusCode.RequestEntityTooLarge, "body_too_large");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Body_Exactly4KiBIsAccepted() {
            string start = "{\"url\":\"" + ThreadUrl + "\"";
            string body = start + new string(' ', 4096 - start.Length - 1) + "}";
            Assert.AreEqual(4096, Encoding.UTF8.GetByteCount(body));
            Assert.AreEqual(HttpStatusCode.Created, PostJson(body).StatusCode);
        }

        [TestMethod]
        public void Url_NotAnAbsoluteHttpUrlWithoutLoginIs422() {
            string[] urls = {
                "file:///etc/passwd", "file://C:/Windows/win.ini", "ftp://boards.4chan.org/wg/thread/1", "javascript:alert(1)", "data:text/html,x",
                "https://user:secret@boards.4chan.org/wg/thread/1", "https://user@boards.4chan.org/wg/thread/1", "boards.4chan.org/wg/thread/1",
                "/wg/thread/1", "", " ", "https://boards.4chan.org", "https://boards.4chan.org/wg/thread/1\nX-Header: 1", "https://boards.4chan.org/wg/thread/1\0",
                "https://boards.4chan.org/" + new string('a', 2049 - "https://boards.4chan.org/".Length), "\\\\host\\share\\x", "C:\\Windows\\x"
            };
            foreach (string url in urls) {
                AssertProblem(AddThread(url), (HttpStatusCode)422, "invalid_url");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Url_2048CharactersIsAccepted() {
            string url = "https://boards.4chan.org/wg/thread/100/";
            url += new string('a', 2048 - url.Length);
            HttpResponseMessage response = AddThread(url);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
        }

        // The known-site check would see another host than the watcher (L1 note): refused, with or without the opt-in
        [TestMethod]
        public void Url_HostThatDiffersFromItsDnsFormIsRefused() {
            foreach (bool allowUnknown in new[] { false, true }) {
                Settings.ApiAllowUnknownHosts = allowUnknown;
                foreach (string url in new[] { "http://４chan.org/wg/thread/1", "http://bücher.4chan.org/wg/thread/1" }) {
                    Uri parsed;
                    if (Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.Host == parsed.IdnHost) {
                        Assert.Fail("Not an IDN case on this platform: " + url + " host " + parsed.Host);
                    }
                    AssertProblem(AddThread(url), (HttpStatusCode)422, "invalid_url");
                }
            }
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Url_UnknownSiteIsRefusedWithoutTheOptIn() {
            TestDns["example.com"] = new[] { IPAddress.Parse(PublicAddress) };
            foreach (string url in new[] { "https://example.com/thread/1", "https://4chan.org.evil.example/wg/thread/1", "https://boards.4chan.org./wg/thread/1", "https://not4chan.org/wg/thread/1" }) {
                AssertProblem(AddThread(url), (HttpStatusCode)422, "unknown_host");
            }
            Assert.AreEqual(0, ResolvedHosts.Count);
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Url_UnknownSiteIsAddedWithTheOptIn() {
            Settings.ApiAllowUnknownHosts = true;
            TestDns["example.com"] = new[] { IPAddress.Parse(PublicAddress), IPAddress.Parse("2001:db8::1") };
            HttpResponseMessage response = AddThread("https://example.com/thread/1");
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            CollectionAssert.Contains(ResolvedHosts, "example.com");
        }

        [TestMethod]
        public void Url_IPAddressesAndLocalNamesAreRefusedWithTheOptIn() {
            Settings.ApiAllowUnknownHosts = true;
            string[] urls = {
                "http://93.184.216.34/thread/1", "http://127.0.0.1/thread/1", "http://10.0.0.1/thread/1", "http://169.254.169.254/latest/meta-data/",
                "http://2130706433/thread/1", "http://0x7f000001/thread/1", "http://localhost/thread/1", "http://LOCALHOST/thread/1", "http://localhost./thread/1",
                "http://nas/thread/1", "http://nas./thread/1", "http://printer.local/x", "http://svc.internal/x", "http://app.localhost/x", "http://a.b.LOCAL/x"
            };
            foreach (string url in urls) {
                TestDns[new Uri(url).IdnHost] = new[] { IPAddress.Parse(PublicAddress) };
                AssertProblem(AddThread(url), (HttpStatusCode)422, "blocked_host");
            }
            HttpResponseMessage ipv6 = AddThread("http://[2606:2800:220:1::1]/thread/1");
            Assert.AreEqual((HttpStatusCode)422, ipv6.StatusCode, Body(ipv6));
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Url_UnknownSiteResolvingToALocalAddressIsRefused() {
            Settings.ApiAllowUnknownHosts = true;
            string[] addresses = { "127.0.0.1", "10.1.2.3", "192.168.1.10", "172.16.0.5", "169.254.169.254", "100.64.0.1", "::1", "fd00::1", "fe80::1", "::ffff:127.0.0.1", "0.0.0.0" };
            foreach (string address in addresses) {
                TestDns["rebind.example"] = new[] { IPAddress.Parse(PublicAddress), IPAddress.Parse(address) };
                AssertProblem(AddThread("https://rebind.example/thread/1"), (HttpStatusCode)422, "blocked_host");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Url_UnknownSiteThatDoesNotResolveIsRefused() {
            Settings.ApiAllowUnknownHosts = true;
            AssertProblem(AddThread("https://nowhere.example/thread/1"), (HttpStatusCode)422, "unresolvable_host");
            TestDns["empty.example"] = new IPAddress[0];
            AssertProblem(AddThread("https://empty.example/thread/1"), (HttpStatusCode)422, "unresolvable_host");
            Assert.AreEqual(0, ThreadCount());
        }

        // L1 review (a): a known site's subdomain that points at a private address is refused too
        [TestMethod]
        public void Url_KnownSiteResolvingToAPrivateAddressIsRefused() {
            TestDns["evil.4chan.org"] = new[] { IPAddress.Parse("192.168.1.10") };
            AssertProblem(AddThread("https://evil.4chan.org/wg/thread/1"), (HttpStatusCode)422, "blocked_host");
            TestDns["boards.4chan.org"] = new[] { IPAddress.Parse(PublicAddress), IPAddress.Parse("127.0.0.1") };
            AssertProblem(AddThread(ThreadUrl), (HttpStatusCode)422, "blocked_host");
            Assert.AreEqual(0, ThreadCount());
        }

        // The watcher reports a known site that does not resolve
        [TestMethod]
        public void Url_KnownSiteThatDoesNotResolveIsAdded() {
            TestDns.Clear();
            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
        }

        // A host registered by a test counts as known; an IP literal registered so still resolves to itself and is refused
        [TestMethod]
        public void Url_RegisteredLoopbackHostIsStillRefused() {
            RegisterHost("127.0.0.1", typeof(FourChanSiteHelper));
            TestDns["127.0.0.1"] = new[] { IPAddress.Loopback };
            AssertProblem(AddThread("http://127.0.0.1/wg/thread/1"), (HttpStatusCode)422, "blocked_host");
        }

        // D8 and G11 in the service itself: whatever the host's factory returns, no login, no folder, and the URL that
        // was checked
        [TestMethod]
        public void Add_LoginAndFolderFromTheFactoryAreDropped() {
            string evilFolder = Path.Combine(Folder, "evil");
            NewThreadFactory = url => {
                ThreadInfo thread = NewThread.Create("https://boards.4chan.org/wg/thread/999", "d", "c");
                thread.PageAuth = "user:secret";
                thread.ImageAuth = "user:secret";
                thread.SaveDir = evilFolder;
                return thread;
            };
            HttpResponseMessage response = AddThread(ThreadUrl);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            ThreadWatcher watcher = Owner.Invoke(() => Session.ThreadWatchers.Single());
            Assert.AreEqual(ThreadUrl, watcher.PageURL);
            Assert.AreEqual(String.Empty, watcher.PageAuth);
            Assert.AreEqual(String.Empty, watcher.ImageAuth);
            Assert.IsFalse((watcher.ThreadDownloadDirectory ?? String.Empty).StartsWith(evilFolder, StringComparison.OrdinalIgnoreCase), watcher.ThreadDownloadDirectory);
            Assert.IsFalse(Directory.Exists(evilFolder));
        }

        // A client that gives up after the lookup answered: the check before the owner thread stops the add
        [TestMethod]
        public void Url_ClientCancelAfterLookupAddsNothing() {
            using (CancellationTokenSource cancel = new CancellationTokenSource()) {
                ApiPolicy policy = new ApiPolicy { GetFreeSpace = () => null };
                policy.ResolveHost = (host, token) => {
                    cancel.Cancel();
                    return Task.FromResult(new[] { IPAddress.Parse(PublicAddress) });
                };
                ApiThreadService service = new ApiThreadService(Session, Dispatcher, url => NewThread.Create(url, null, null), policy);
                Assert.Throws<OperationCanceledException>(() => service.AddAsync(new Uri(ThreadUrl), cancel.Token).GetAwaiter().GetResult());
            }
            Assert.AreEqual(0, ThreadCount());
        }

        // A client that gives up during the lookup is not "the lookup failed": for an unknown site that would be a 422
        // (unresolvable_host) answer to a client that is gone, for a known one an add. The lookup's filter lets the
        // cancellation through.
        [TestMethod]
        public void Url_ClientCancelDuringLookupIsNotALookupFailure() {
            Settings.ApiAllowUnknownHosts = true;
            using (CancellationTokenSource cancel = new CancellationTokenSource()) {
                ApiPolicy policy = new ApiPolicy { GetFreeSpace = () => null };
                policy.ResolveHost = (host, token) => {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new[] { IPAddress.Parse(PublicAddress) });
                };
                ApiThreadService service = new ApiThreadService(Session, Dispatcher, url => NewThread.Create(url, null, null), policy);
                Assert.Throws<OperationCanceledException>(() => service.AddAsync(new Uri("https://example.com/thread/1"), cancel.Token).GetAwaiter().GetResult());
            }
            Assert.AreEqual(0, ThreadCount());
        }

        // Kestrel's minimum body data rate (240 bytes/s after a 5 s grace) ends a stalled body: 408 request_timeout
        [TestMethod]
        public void Body_StalledBodyIs408() {
            string port = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient()) {
                client.Connect(IPAddress.Loopback, Port);
                client.ReceiveTimeout = 30000;
                byte[] request = Encoding.ASCII.GetBytes("POST " + ThreadsEndpoints.ThreadsPath + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + Token +
                    "\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"url\":");
                client.GetStream().Write(request, 0, request.Length);
                string response = new StreamReader(client.GetStream(), Encoding.ASCII).ReadToEnd();
                StringAssert.StartsWith(response, "HTTP/1.1 408");
                StringAssert.Contains(response, "\"code\":\"request_timeout\"");
            }
            Assert.AreEqual(0, ThreadCount());
        }

        // The unknown-sites setting turned off while the lookup ran: the add is refused on the owner thread
        [TestMethod]
        public void Url_OptOutDuringLookupRefusesTheUnknownSite() {
            Settings.ApiAllowUnknownHosts = true;
            Policy.ResolveHost = (host, token) => {
                Settings.ApiAllowUnknownHosts = false;
                return Task.FromResult(new[] { IPAddress.Parse(PublicAddress) });
            };
            AssertProblem(AddThread("https://example.com/thread/1"), (HttpStatusCode)422, "unknown_host");
            Assert.AreEqual(0, ThreadCount());
        }

        // Each é is 2 characters in the request but 6 once escaped, as the watcher would use the URL
        [TestMethod]
        public void Url_CleanedUrlOverTheLimitIsRefused() {
            string url = "https://boards.4chan.org/wg/thread/100/" + new string('é', 600);
            Assert.IsTrue(url.Length <= 2048);
            Assert.IsTrue(new Uri(url).AbsoluteUri.Length > 2048);
            // Sent as UTF-8 (2 bytes each), not as \u escapes, so the body stays under 4 KiB
            AssertProblem(PostJson("{\"url\":\"" + url + "\"}"), (HttpStatusCode)422, "invalid_url");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Add_DuplicateIs409AndLeavesTheThreadAsItIs() {
            Assert.AreEqual(HttpStatusCode.Created, AddThread(ThreadUrl).StatusCode);
            ThreadWatcher watcher = Owner.Invoke(() => Session.ThreadWatchers[0]);
            Owner.Invoke(() => {
                watcher.Description = "kept";
                watcher.Stop(StopReason.UserRequest);
            });
            foreach (string url in new[] { ThreadUrl, "http://boards.4chan.org/wg/thread/100", "https://boards.4chan.org/wg/thread/100/some-slug", ThreadUrl + "#p123" }) {
                AssertProblem(AddThread(url), HttpStatusCode.Conflict, "already_watched");
            }
            Assert.AreEqual(1, ThreadCount());
            Assert.AreSame(watcher, Owner.Invoke(() => Session.ThreadWatchers[0]));
            Assert.AreEqual("kept", watcher.Description);
            Assert.AreEqual(StopReason.UserRequest, watcher.StopReason);
            Assert.AreEqual(ThreadUrl, watcher.PageURL);
        }

        [TestMethod]
        public void Add_BlacklistedIs409() {
            Owner.Invoke(() => Session.AddBlacklistRules(new[] { "4chan/wg/100" }));
            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.Conflict, "blacklisted");
            Assert.AreEqual(0, ThreadCount());
        }

        [TestMethod]
        public void Add_FreeSpaceUnderTheFloorIs507() {
            Policy.GetFreeSpace = () => ApiPolicy.DefaultFreeSpaceFloorBytes - 1;
            AssertProblem(AddThread(ThreadUrl), (HttpStatusCode)507, "insufficient_storage");
            Assert.AreEqual(0, ThreadCount());
            Policy.GetFreeSpace = () => ApiPolicy.DefaultFreeSpaceFloorBytes;
            Assert.AreEqual(HttpStatusCode.Created, AddThread(ThreadUrl).StatusCode);
        }

        [TestMethod]
        public void Add_WhileTheHostExitsIs503() {
            Policy.IsExiting = () => true;
            AssertProblem(AddThread(ThreadUrl), HttpStatusCode.ServiceUnavailable, "unavailable");
            Assert.AreEqual(0, ThreadCount());
        }

        private sealed class NonSeekableStream : MemoryStream {
            public NonSeekableStream(byte[] bytes)
                : base(bytes) { }

            public override bool CanSeek {
                get { return false; }
            }
        }
    }

    // The thread cap, with a small cap so the test does not add 1000 threads
    [TestClass]
    public class ThreadCapTests : ApiTestBase {
        internal override void ConfigurePolicy(ApiPolicy policy) {
            policy.ThreadCap = 2;
        }

        [TestMethod]
        public void Add_AtTheCapIs409() {
            Assert.AreEqual(HttpStatusCode.Created, AddThread("https://boards.4chan.org/wg/thread/1").StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, AddThread("https://boards.4chan.org/wg/thread/2").StatusCode);
            AssertProblem(AddThread("https://boards.4chan.org/wg/thread/3"), HttpStatusCode.Conflict, "thread_limit");
            Assert.AreEqual(2, ThreadCount());
        }
    }

    // The windows are an hour long here, so a test never spans two of them
    [TestClass]
    public class RateLimitTests : ApiTestBase {
        internal override void ConfigurePolicy(ApiPolicy policy) {
            policy.RateWindow = TimeSpan.FromHours(1);
        }

        // Only authenticated requests count: refused ones (no token, another origin, a query string) never use up
        // the limit of the real client
        [TestMethod]
        public void Requests_RefusedRequestsDoNotUseTheLimit() {
            using (HttpClient anonymous = CreateClient(Port)) {
                for (int i = 0; i < 130; i++) {
                    Assert.AreEqual(HttpStatusCode.Forbidden, Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.Add("Origin", "http://evil.example")).StatusCode);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.GetAsync(ThreadsEndpoints.ThreadsPath).GetAwaiter().GetResult().StatusCode);
                    Assert.AreEqual(HttpStatusCode.BadRequest, Get(ThreadsEndpoints.ThreadsPath + "?x=1").StatusCode);
                }
            }
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }
        [TestMethod]
        public void Limits_AreTheDesignValues() {
            ApiPolicy defaults = new ApiPolicy();
            Assert.AreEqual(4096, defaults.MaxBodyBytes);
            Assert.AreEqual(2048, defaults.MaxUrlLength);
            Assert.AreEqual(120, defaults.RequestsPerMinute);
            Assert.AreEqual(30, defaults.AddsPerMinute);
            Assert.AreEqual(1000, defaults.ThreadCap);
            Assert.AreEqual(1024L * 1024 * 1024, defaults.FreeSpaceFloorBytes);
            Assert.AreEqual(TimeSpan.FromSeconds(5), defaults.OwnerThreadTimeout);
            Assert.AreEqual(TimeSpan.FromMinutes(1), defaults.RateWindow);
        }

        [TestMethod]
        public void Add_31stAddInAMinuteIs429WithRetryAfter() {
            for (int i = 1; i <= 30; i++) {
                HttpResponseMessage response = AddThread("https://boards.4chan.org/wg/thread/" + i);
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, i + ": " + Body(response));
            }
            HttpResponseMessage limited = AddThread("https://boards.4chan.org/wg/thread/31", "203.0.113.99");
            AssertProblem(limited, (HttpStatusCode)429, "add_rate_limited");
            AssertRetryAfter(limited);
            Assert.AreEqual(30, ThreadCount());
            // Listing still works
            Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode);
        }

        // One global limit: no forwarded header gives a client a fresh one
        [TestMethod]
        public void Requests_121stRequestInAMinuteIs429AndForwardedHeadersDoNotReset() {
            for (int i = 1; i <= 120; i++) {
                Assert.AreEqual(HttpStatusCode.OK, Get(ThreadsEndpoints.ThreadsPath).StatusCode, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            string[] spoofed = { "X-Forwarded-For", "X-Real-IP", "Forwarded", "X-Client-IP", "True-Client-IP" };
            foreach (string header in spoofed) {
                HttpResponseMessage response = Get(ThreadsEndpoints.ThreadsPath, request => request.Headers.TryAddWithoutValidation(header, header == "Forwarded" ? "for=203.0.113.7" : "203.0.113.7"));
                AssertProblem(response, (HttpStatusCode)429, "rate_limited");
                AssertRetryAfter(response);
            }
            AssertProblem(AddThread(ThreadUrl), (HttpStatusCode)429, "rate_limited");
            Assert.AreEqual(0, ThreadCount());
        }

        private HttpResponseMessage AddThread(string url, string forwardedFor) {
            return PostJson("{\"url\":\"" + url + "\"}", request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor));
        }

        private static void AssertRetryAfter(HttpResponseMessage response) {
            TimeSpan? delta = response.Headers.RetryAfter?.Delta;
            Assert.IsTrue(delta.HasValue && delta.Value > TimeSpan.Zero && delta.Value <= TimeSpan.FromHours(1), "Retry-After: " + response.Headers.RetryAfter);
        }
    }
}
