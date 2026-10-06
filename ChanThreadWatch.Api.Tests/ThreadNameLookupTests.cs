using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Api.Tests {
    // With the slug setting on, a 4chan URL without the slug makes the watcher's constructor download the page on the
    // owner thread (the UI thread in the app) to learn it. An add looks it up first, on the request's thread, on the
    // guarded clients and within a time limit, and checks the URL the page names as it checks the request's; the
    // watcher then never downloads it. A lookup that fails or takes too long adds the request's URL.
    [TestClass]
    public class ThreadNameLookupTests : ApiTestBase {
        private const string SlugUrl = ThreadUrl + "/a-slug";
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        [TestInitialize]
        public void UseSlugs() {
            Settings.UseSlug = true;
        }

        private static string Page(string canonicalUrl) {
            return "<html><head><title>t</title><link rel=\"canonical\" href=\"" + canonicalUrl + "\"></head><body></body></html>";
        }

        private ThreadWatcher OnlyWatcher() {
            return Owner.Invoke(() => Session.ThreadWatchers.Single());
        }

        // The owner thread stays free while the lookup is held; the thread is added with the URL the page names, and
        // its name has the slug
        [TestMethod]
        public void ASlowLookupDoesNotHoldTheOwnerThread() {
            using (ManualResetEventSlim held = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                Policy.FetchThreadPage = async (url, token) => {
                    held.Set();
                    await Task.Run(() => release.Wait(Wait));
                    return Page(SlugUrl);
                };
                Task<HttpResponseMessage> add = Task.Run(() => AddThread(ThreadUrl));
                Assert.IsTrue(held.Wait(Wait), "The page was not looked up before the add.");

                bool ownerFree = Task.Run(() => Owner.Invoke(() => { })).Wait(TimeSpan.FromSeconds(5));
                release.Set();

                Assert.IsTrue(ownerFree, "The owner thread was held by the lookup.");
                HttpResponseMessage response = add.Result;
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
                Assert.AreEqual(SlugUrl, Json(response).GetProperty("url").GetString());
            }
            ThreadWatcher watcher = OnlyWatcher();
            Assert.AreEqual(SlugUrl, watcher.PageURL);
            StringAssert.Contains(watcher.ThreadName, "a-slug");
            Assert.AreEqual("4chan/wg/100", watcher.PageID);
        }

        // A lookup that takes too long or fails adds the request's URL, named by its number, and the watcher's
        // constructor never downloads the page on the owner thread: every connection's lookup is held meanwhile, so a
        // download there would hold the add too
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ALookupThatTimesOutOrFailsStillAddsWithoutTheOwnerThreadDownloading(bool hangs) {
            using (ManualResetEventSlim release = new ManualResetEventSlim(false)) {
                SSRFGuard.ResolveHost = (host, token) => Task.Run(new Func<IPAddress[]>(() => {
                    release.Wait(Wait);
                    throw new SocketException((int)SocketError.HostNotFound);
                }));
                Policy.ThreadNameLookupTimeout = TimeSpan.FromMilliseconds(200);
                Policy.FetchThreadPage = (url, token) => hangs ? new TaskCompletionSource<string>().Task : Task.FromException<string>(new IOException("failed"));

                Task<HttpResponseMessage> add = Task.Run(() => AddThread(ThreadUrl));
                bool added = add.Wait(TimeSpan.FromSeconds(10));
                release.Set();

                Assert.IsTrue(added, "The add waited for a download on the owner thread.");
                Assert.AreEqual(HttpStatusCode.Created, add.Result.StatusCode, Body(add.Result));
            }
            ThreadWatcher watcher = OnlyWatcher();
            Assert.AreEqual(ThreadUrl, watcher.PageURL);
            Assert.AreEqual("100", watcher.ThreadName);
        }

        // The URL the page names must be the same thread on the same host and pass the URL rules; otherwise the request's
        // URL is added
        [TestMethod]
        [DataRow("https://other.example/wg/thread/100/a-slug")]
        [DataRow("https://boards.4chan.org/wg/thread/999/a-slug")]
        [DataRow("https://203.0.113.10/wg/thread/100/a-slug")]
        [DataRow("https://name:password@boards.4chan.org/wg/thread/100/a-slug")]
        [DataRow("ftp://boards.4chan.org/wg/thread/100/a-slug")]
        [DataRow("https://boards.4chan.org/wg/thread/100/LONG")]
        // Another 4chan host with the same page ID ("4chan/wg/100"), which resolves to a public address
        [DataRow("https://sys.4chan.org/wg/thread/100/a-slug")]
        // Never https to http, and never another port
        [DataRow("http://boards.4chan.org/wg/thread/100/a-slug")]
        [DataRow("http://boards.4chan.org:443/wg/thread/100/a-slug")]
        [DataRow("https://boards.4chan.org:8443/wg/thread/100/a-slug")]
        public void ANamedUrlThatFailsTheRulesIsNotTaken(string canonicalUrl) {
            canonicalUrl = canonicalUrl.Replace("LONG", new string('a', ApiPolicy.DefaultMaxUrlLength));
            TestDns["sys.4chan.org"] = new[] { IPAddress.Parse(PublicAddress) };
            Policy.FetchThreadPage = (url, token) => Task.FromResult(Page(canonicalUrl));

            HttpResponseMessage response = AddThread(ThreadUrl);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            Assert.AreEqual(ThreadUrl, OnlyWatcher().PageURL);
        }

        // Known difference from the watcher's own lookup: a 4channel.org thread whose page names its 4chan.org URL keeps
        // the request's URL and is named by its number, since the other host gives another page ID ("4chan/..." in
        // place of "4channel/...")
        [TestMethod]
        public void ACanonicalLinkToAnotherHostOfTheSameSiteIsNotTaken() {
            const string channelUrl = "https://boards.4channel.org/wg/thread/100";
            TestDns["boards.4channel.org"] = new[] { IPAddress.Parse(PublicAddress) };
            Policy.FetchThreadPage = (url, token) => Task.FromResult(Page(SlugUrl));

            HttpResponseMessage response = AddThread(channelUrl);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            ThreadWatcher watcher = OnlyWatcher();
            Assert.AreEqual(channelUrl, watcher.PageURL);
            Assert.AreEqual("100", watcher.ThreadName);
        }

        // A page that the site helper can't read (here a helper whose parse throws) adds the request's URL, not a 500
        [TestMethod]
        public void APageThatCannotBeReadStillAdds() {
            const string throwingUrl = "https://throwing.ctw.test/wg/thread/100";
            RegisterHost("throwing.ctw.test", typeof(ThrowingThreadNameSiteHelper));
            TestDns["throwing.ctw.test"] = new[] { IPAddress.Parse(PublicAddress) };
            Policy.FetchThreadPage = (url, token) => Task.FromResult(Page(SlugUrl));

            HttpResponseMessage response = AddThread(throwingUrl);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            Assert.AreEqual(throwingUrl, OnlyWatcher().PageURL);
        }

        // The named URL's host is looked up again, as the request's was: an answer with a private address keeps the
        // request's URL
        [TestMethod]
        public void ANamedUrlWhoseHostNowResolvesToAPrivateAddressIsNotTaken() {
            int lookups = 0;
            Policy.ResolveHost = (host, token) => Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref lookups) == 1 ? PublicAddress : "10.0.0.1") });
            Policy.FetchThreadPage = (url, token) => Task.FromResult(Page(SlugUrl));

            HttpResponseMessage response = AddThread(ThreadUrl);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            Assert.AreEqual(2, lookups);
            Assert.AreEqual(ThreadUrl, OnlyWatcher().PageURL);
        }

        // The real lookup goes out on the guarded clients: a host that now resolves to loopback is refused before any
        // socket connects (the download fails with BlockedAddressException, and BeforeConnect never sees the address),
        // and the thread is still added
        [TestMethod]
        public void TheLookupIsGuarded() {
            Func<string, CancellationToken, Task<string>> realFetch = Policy.FetchThreadPage;
            Exception fetchError = null;
            Policy.FetchThreadPage = async (url, token) => {
                try {
                    return await realFetch(url, token);
                }
                catch (Exception ex) {
                    fetchError = ex;
                    throw;
                }
            };
            List<string> connectLookups = new List<string>();
            List<IPAddress> connected = new List<IPAddress>();
            SSRFGuard.ResolveHost = (host, token) => {
                lock (connectLookups) connectLookups.Add(host);
                return Task.FromResult(new[] { IPAddress.Loopback });
            };
            SSRFGuard.BeforeConnect = addresses => {
                lock (connected) connected.AddRange(addresses);
                throw new InvalidOperationException("The API tests never connect.");
            };

            HttpResponseMessage response = AddThread(ThreadUrl);

            List<IPAddress> connectedDuringAdd;
            lock (connected) connectedDuringAdd = connected.ToList();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, Body(response));
            lock (connectLookups) CollectionAssert.Contains(connectLookups, "boards.4chan.org");
            Assert.AreEqual(0, connectedDuringAdd.Count, "The lookup connected without the guard.");
            Assert.IsInstanceOfType<BlockedAddressException>(fetchError);
            Assert.AreEqual(ThreadUrl, OnlyWatcher().PageURL);
        }

        // A client that gives up during the lookup ends it, and nothing is added
        [TestMethod]
        public void AClientThatGivesUpEndsTheLookup() {
            Policy.ThreadNameLookupTimeout = TimeSpan.FromMinutes(5);
            using (ManualResetEventSlim started = new ManualResetEventSlim(false))
            using (ManualResetEventSlim canceled = new ManualResetEventSlim(false)) {
                Policy.FetchThreadPage = async (url, token) => {
                    token.Register(canceled.Set);
                    started.Set();
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                    return Page(SlugUrl);
                };
                using (CancellationTokenSource giveUp = new CancellationTokenSource())
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ThreadsEndpoints.ThreadsPath)) {
                    request.Content = new StringContent(JsonSerializer.Serialize(new { url = ThreadUrl }), System.Text.Encoding.UTF8, "application/json");
                    Task<HttpResponseMessage> add = Client.SendAsync(request, giveUp.Token);
                    Assert.IsTrue(started.Wait(Wait));
                    giveUp.Cancel();
                    Assert.Throws<OperationCanceledException>(() => add.GetAwaiter().GetResult());
                }
                Assert.IsTrue(canceled.Wait(Wait), "The lookup was not ended when the client gave up.");
            }
            Thread.Sleep(200);
            Assert.AreEqual(0, ThreadCount());
        }
    }

    // A 4chan helper that always needs the name and can't read any page, for APageThatCannotBeReadStillAdds
    public class ThrowingThreadNameSiteHelper : FourChanSiteHelper {
        public override bool NeedsThreadNameLookup() {
            return true;
        }

        public override string GetURLWithThreadName(string page) {
            throw new InvalidOperationException("The page can't be read.");
        }
    }
}
