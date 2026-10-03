using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests.Integration {
    // Downloads each sanitized site fixture (Fixtures/sites) end to end with the real ThreadWatcher
    // and the fixture's site helper. The page, images and thumbnails are all served by one loopback
    // server, and every {{md5_N}} is the MD5 of the bytes served for the images that carry it.
    [TestClass]
    public class SiteFixtureDownloadTests : ThreadWatcherIntegrationTestBase {
        public static IEnumerable<object[]> Fixtures => SiteFixtures.Names;

        [TestMethod]
        [DynamicData(nameof(Fixtures))]
        public void DownloadsFixtureThread(string name) {
            Dictionary<string, object> entry = SiteFixtures.Entry(name);
            Type helperType = typeof(SiteHelper).Assembly.GetType("JDP." + (string)entry["helper"], true);
            SiteHelpers.RegisterHostForTesting(PageHost, helperType);
            LoopbackHttpServer server = StartServer();
            string pageURL = server.URL((string)entry["pagePath"]);
            var fixture = new SiteFixtureDownload(name, server.BaseURL(), pageURL, helperType);
            fixture.RouteAll(server);
            ThreadWatcher watcher = CreateWatcher(pageURL);

            StopReason reason = RunToStop(watcher);

            Assert.AreEqual(StopReason.DownloadComplete, reason, name);
            Assert.AreEqual(0, watcher.FailedFileCount, name);
            Assert.IsNull(watcher.CheckError, name);
            Assert.IsNull(watcher.StopError, name);
            Assert.HasCount((int)entry["images"], fixture.Images, name + " images");
            Assert.AreEqual((int)entry["hashes"], fixture.Images.Count(i => i.Hash != null && i.HashType == HashType.MD5), name + " hashes");
            Assert.HasCount((int)entry["thumbnails"], fixture.Thumbnails, name + " thumbnails");
            AssertImagesSaved(name, server, fixture, watcher.ThreadDownloadDirectory);
            AssertThumbnailsSaved(name, server, fixture, watcher.ThreadDownloadDirectory);
            string savedPagePath = Path.Combine(watcher.ThreadDownloadDirectory, General.CleanFileName(watcher.ThreadName) + ".html");
            AssertSavedPageLinksLocalFiles(name, fixture, savedPagePath);
            OfflinePageScriptTests.AssertHasOfflineScript(File.ReadAllText(savedPagePath), ((SiteHelper)Activator.CreateInstance(helperType)).GetOfflinePageScriptSite(), name);        }

        // Each image is requested once, so its hash matched the first time (a mismatch is retried),
        // and is saved under its URL file name with exactly the served bytes
        private static void AssertImagesSaved(string name, LoopbackHttpServer server, SiteFixtureDownload fixture, string threadDir) {
            foreach (KeyValuePair<string, byte[]> image in fixture.ImageBytes) {
                Assert.HasCount(1, server.RequestsTo(image.Key), name + " " + image.Key);
            }
            var expectedFiles = new List<string>();
            foreach (ImageInfo image in fixture.Images) {
                CollectionAssert.AreEqual(fixture.ImageBytes[PathOf(image.URL)], File.ReadAllBytes(Path.Combine(threadDir, image.FileName)), name + " " + image.URL);
                expectedFiles.Add(image.FileName);
            }
            string[] savedFiles = Directory.GetFiles(threadDir).Select(Path.GetFileName).Where(f => !f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToArray();
            CollectionAssert.AreEquivalent(expectedFiles.Distinct().ToList(), savedFiles, name + " saved images");
        }

        private static void AssertThumbnailsSaved(string name, LoopbackHttpServer server, SiteFixtureDownload fixture, string threadDir) {
            foreach (KeyValuePair<string, byte[]> thumb in fixture.ThumbnailBytes) {
                Assert.HasCount(1, server.RequestsTo(thumb.Key), name + " " + thumb.Key);
            }
            foreach (ThumbnailInfo thumb in fixture.Thumbnails) {
                CollectionAssert.AreEqual(fixture.ThumbnailBytes[PathOf(thumb.URL)], File.ReadAllBytes(Path.Combine(threadDir, "thumbs", thumb.FileName)), name + " " + thumb.URL);
            }
            Assert.HasCount(fixture.ThumbnailBytes.Count, Directory.GetFiles(Path.Combine(threadDir, "thumbs")), name + " saved thumbnails");
        }

        // Every image and thumbnail is linked by its local file, and no link in the saved page
        // still points at a downloaded file on the server
        private static void AssertSavedPageLinksLocalFiles(string name, SiteFixtureDownload fixture, string savedPagePath) {
            string html = File.ReadAllText(savedPagePath);
            Assert.DoesNotContain("{{", html, name);
            var hrefs = new HashSet<string>(StringComparer.Ordinal);
            var srcs = new HashSet<string>(StringComparer.Ordinal);
            var serverLinks = new List<string>();
            foreach (HTMLTag tag in new HTMLParser(html).Tags) {
                foreach (HTMLAttribute attribute in tag.Attributes) {
                    string value = HttpUtility.HtmlDecode(attribute.Value);
                    if (attribute.NameEquals("href")) hrefs.Add(value);
                    else if (attribute.NameEquals("src")) srcs.Add(value);
                    else continue;
                    if (fixture.IsServedFile(value)) serverLinks.Add(attribute.Name + "=\"" + value + "\"");
                }
            }
            foreach (ImageInfo image in fixture.Images) {
                Assert.IsTrue(hrefs.Contains(image.FileName), name + " link to " + image.URL);
            }
            foreach (ThumbnailInfo thumb in fixture.Thumbnails) {
                Assert.IsTrue(srcs.Contains("thumbs/" + thumb.FileName), name + " link to " + thumb.URL);
            }
            Assert.IsEmpty(serverLinks, name + " links not rewritten:\n" + String.Join("\n", serverLinks.Take(20)));
        }

        private static string PathOf(string url) => new Uri(url).PathAndQuery;

        // A fixture page with its placeholders filled in for one loopback server, the files the
        // site helper finds in it, and the bytes served for each of them
        private sealed class SiteFixtureDownload {
            private readonly Dictionary<int, byte[]> _md5Bytes = new Dictionary<int, byte[]>();
            private readonly Dictionary<string, byte[]> _bytesByMD5 = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private readonly string _pagePath;
            private readonly string _pageURL;

            public SiteFixtureDownload(string name, string baseURL, string pageURL, Type helperType) {
                _pageURL = pageURL;
                _pagePath = PathOf(pageURL);
                Html = SiteFixtures.Substitute(SiteFixtures.ReadFixture(name), baseURL, baseURL, MD5Placeholder);
                var helper = (SiteHelper)Activator.CreateInstance(helperType);
                helper.SetURL(pageURL);
                helper.SetHTMLParser(new HTMLParser(Html));
                Thumbnails = new List<ThumbnailInfo>();
                Images = helper.GetImages(null, Thumbnails);
                foreach (ImageInfo image in Images) {
                    if (image.Hash != null) AddHashedBytes(image);
                    else AddBytes(ImageBytes, image.URL, 100000, 1000);
                }
                foreach (ThumbnailInfo thumb in Thumbnails) {
                    AddBytes(ThumbnailBytes, thumb.URL, 200000, 300);
                }
            }

            public string Html { get; }

            public List<ImageInfo> Images { get; }

            public List<ThumbnailInfo> Thumbnails { get; }

            // Bytes served for each image and thumbnail, by server path
            public Dictionary<string, byte[]> ImageBytes { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public Dictionary<string, byte[]> ThumbnailBytes { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public void RouteAll(LoopbackHttpServer server) {
                server.Route(_pagePath, LoopbackResponse.Html(Html));
                foreach (KeyValuePair<string, byte[]> image in ImageBytes) {
                    server.Route(image.Key, LoopbackResponse.Bytes(image.Value, "image/jpeg"));
                }
                foreach (KeyValuePair<string, byte[]> thumb in ThumbnailBytes) {
                    server.Route(thumb.Key, LoopbackResponse.Bytes(thumb.Value, "image/jpeg"));
                }
            }

            // True if the link, resolved against the page URL, is an image or thumbnail on the server
            public bool IsServedFile(string link) {
                Uri uri;
                if (!Uri.TryCreate(new Uri(_pageURL), link, out uri) || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp) return false;
                return ImageBytes.ContainsKey(uri.PathAndQuery) || ThumbnailBytes.ContainsKey(uri.PathAndQuery);
            }

            // A file without a hash (a thumbnail, or a LynxChan image) gets deterministic bytes
            // per distinct path, so a URL that appears more than once (e.g. a shared spoiler
            // thumbnail) is served the same bytes each time
            private static void AddBytes(Dictionary<string, byte[]> files, string url, int seed, int length) {
                string path = PathOf(url);
                if (!files.ContainsKey(path)) files.Add(path, FourChanThreadFixture.MakeBytes(seed + files.Count, length + files.Count));
            }

            // An image with a hash gets the bytes of its MD5 placeholder. Two images at one URL
            // must carry the same hash.
            private void AddHashedBytes(ImageInfo image) {
                byte[] bytes;
                Assert.IsTrue(_bytesByMD5.TryGetValue(Convert.ToBase64String(image.Hash), out bytes), "No placeholder has the hash of " + image.URL);
                string path = PathOf(image.URL);
                byte[] existing;
                if (ImageBytes.TryGetValue(path, out existing)) {
                    Assert.AreSame(existing, bytes, "Two different hashes for " + image.URL);
                    return;
                }
                ImageBytes.Add(path, bytes);
            }

            private string MD5Placeholder(int index) {
                byte[] bytes;
                if (!_md5Bytes.TryGetValue(index, out bytes)) {
                    bytes = FourChanThreadFixture.MakeBytes(index, 1000 + index);
                    _md5Bytes.Add(index, bytes);
                    _bytesByMD5.Add(MD5Base64(bytes), bytes);
                }
                return MD5Base64(bytes);
            }

            private static string MD5Base64(byte[] data) {
                using (MD5 md5 = MD5.Create()) {
                    return Convert.ToBase64String(md5.ComputeHash(data));
                }
            }
        }
    }
}
