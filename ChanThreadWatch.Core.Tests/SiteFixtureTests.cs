using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Fixtures/sites holds real thread markup from each supported site kind, sanitized by
    // tools/site-fixtures/sanitize_site_fixture.py. These tests keep it sterile and check that the
    // site helpers still find what the sanitizer verified against the original capture.
    [TestClass]
    public class SiteFixtureTests {
        public static IEnumerable<object[]> Fixtures => SiteFixtures.Names;

        // Every value a {{md5_N}}, {{md5u_N}} or {{md5s_N}} placeholder can stand for
        private static readonly Lazy<HashSet<string>> PlaceholderMD5s = new Lazy<HashSet<string>>(
            () => new HashSet<string>(System.Linq.Enumerable.Range(1, 10000).Select(SiteFixtures.PlaceholderMD5)));

        // Nothing else may sit in the fixture folder (for example a raw capture), because only the
        // listed fixtures are checked for real data
        [TestMethod]
        public void FolderHoldsOnlyListedFixtures() {
            string[] files = System.IO.Directory.GetFiles(SiteFixtures.Directory, "*", System.IO.SearchOption.AllDirectories)
                .Select(f => f.Substring(SiteFixtures.Directory.Length + 1)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            string[] expected = SiteFixtures.Names.Select(n => (string)n[0] + ".html").Concat(new[] { "allowlist.json", "manifest.json" })
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expected, files);
            Assert.IsGreaterThanOrEqualTo(5, SiteFixtures.Names.Count());
        }

        // The sterility check reads its grammar from allowlist.json, so a loosened pattern there
        // would weaken it without any test failing. Changing the grammar means changing this test.
        [TestMethod]
        public void AllowlistGrammarIsPinned() {
            var grammar = (Dictionary<string, object>)SiteFixtures.Allowlist["grammar"];

            CollectionAssert.AreEquivalent(new Dictionary<string, object> {
                { "number", "777[0-9]{7}" },
                { "word", "w[0-9]{1,4}" },
                { "hex", "f{10,}[0-9]{6}" },
                { "fileName", @"file-[0-9]{1,4}\.(?:jpg|jpeg|png|gif|webp|webm|mp4|html|spoiler|bin)" },
                { "index", "[0-9]{1,4}" },
                { "md5", @"\{\{md5_[0-9]{1,4}\}\}" },
                { "md5UrlSafe", @"\{\{md5u_[0-9]{1,4}\}\}" },
                { "md5Standard", @"\{\{md5s_[0-9]{1,4}\}\}" },
                { "host", @"\{\{(base|media)\}\}" }
            }.ToList(), grammar.ToList());
        }

        [TestMethod]
        [DynamicData(nameof(Fixtures))]
        public void ManifestEntryHoldsOnlyPlaceholders(string name) {
            Dictionary<string, object> entry = SiteFixtures.Entry(name);

            StringAssert.Matches(name, new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"));
            CollectionAssert.AreEquivalent(new[] { "helper", "pagePath", "capture", "captured", "images", "hashes", "thumbnails", "crossLinks" }, entry.Keys.ToArray(), name);
            Assert.IsTrue(typeof(SiteHelper).IsAssignableFrom(typeof(SiteHelper).Assembly.GetType("JDP." + (string)entry["helper"], true)), name);
            Assert.IsTrue(SiteFixtures.IsPlaceholderURL((string)entry["pagePath"]), name + " pagePath");
            CollectionAssert.Contains(new[] { "direct", "firecrawl" }, entry["capture"], name);
            StringAssert.Matches((string)entry["captured"], new Regex(@"^\d{4}-\d{2}-\d{2}$"), name);
        }

        [TestMethod]
        [DynamicData(nameof(Fixtures))]
        public void FixtureIsSterile(string name) {
            List<string> violations = SiteFixtures.FindViolations(SiteFixtures.ReadFixture(name));

            Assert.IsEmpty(violations, name + ":\n" + String.Join("\n", violations.Take(20)));
        }

        [TestMethod]
        [DynamicData(nameof(Fixtures))]
        public void HelperFindsTheVerifiedImages(string name) {
            Dictionary<string, object> entry = SiteFixtures.Entry(name);
            SiteHelper helper = CreateHelper(name);
            var thumbnails = new List<ThumbnailInfo>();

            List<ImageInfo> images = helper.GetImages(null, thumbnails);

            Assert.IsTrue(helper.IsThreadPage(), name);
            Assert.AreEqual((int)entry["images"], images.Count, name + " images");
            Assert.AreEqual((int)entry["hashes"], images.Count(i => i.Hash != null), name + " hashes");
            Assert.AreEqual((int)entry["thumbnails"], thumbnails.Count, name + " thumbnails");
            Assert.AreEqual((int)entry["crossLinks"], helper.GetCrossLinks(null, true).Count, name + " cross links");
            Assert.IsGreaterThan(0, images.Count, name);
        }

        // The names, hashes and URLs the helper returns are the placeholders, so the saved files
        // of a download from these fixtures have predictable names
        [TestMethod]
        [DynamicData(nameof(Fixtures))]
        public void HelperReadsPlaceholderValues(string name) {
            List<ImageInfo> images = CreateHelper(name).GetImages(null, new List<ThumbnailInfo>());
            HashSet<string> md5s = PlaceholderMD5s.Value;

            foreach (ImageInfo image in images) {
                StringAssert.Matches(image.OriginalFileName, new Regex(@"^file-\d+\.[a-z0-9]+$"), name);
                StringAssert.Matches(image.URL, new Regex("^(" + Regex.Escape(SiteFixtures.BaseURL) + "|" + Regex.Escape(SiteFixtures.MediaURL) + ")/"), name);
                Assert.IsTrue(image.Hash == null || md5s.Contains(Convert.ToBase64String(image.Hash)), name + " hash");
                Assert.IsTrue(image.Poster == "" || Regex.IsMatch(image.Poster, @"^(name-\d+(!trip-\d+)?|!trip-\d+|(ID:)?id-\d+|Anonymous!trip-\d+)$"), name + " poster " + image.Poster);
            }
        }

        // On warosu a reply's first span is the poster name inside its label; the file info is the
        // span with the fileinfo class after it, so reply images are found as well as the OP's
        [TestMethod]
        public void FuukaFindsReplyImages() {
            List<ImageInfo> images = CreateHelper("fuuka-base64-md5").GetImages(null, new List<ThumbnailInfo>());

            CollectionAssert.AreEqual(new[] { "file-1.jpg", "file-2.png", "file-3.jpg" }, images.Select(i => i.OriginalFileName).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }.Select(SiteFixtures.PlaceholderMD5).ToArray(), images.Select(i => Convert.ToBase64String(i.Hash)).ToArray());
        }

        // The OP's "same image" link carries its MD5 in standard base64 ({{md5s_1}}), so the path
        // after /image/ holds "/" and "+" and the whole of it is the MD5
        [TestMethod]
        public void FuukaReadsStandardBase64MD5FromSameImageLink() {
            string fixture = SiteFixtures.ReadFixture("fuuka-base64-md5");
            StringAssert.Contains(fixture, "/image/{{md5s_1}}\"");
            string md5 = SiteFixtures.PlaceholderMD5(1);
            StringAssert.Contains(SiteFixtures.Substitute(fixture), "/image/" + md5.TrimEnd('=') + "\"");

            List<ImageInfo> images = CreateHelper("fuuka-base64-md5").GetImages(null, new List<ThumbnailInfo>());

            Assert.AreEqual(HashType.MD5, images[0].HashType);
            Assert.AreEqual(md5, Convert.ToBase64String(images[0].Hash));
            StringAssert.Matches(md5, new Regex(@"^(?=.*/)(?=.*\+)"));
        }

        [TestMethod]
        [DataRow("<div class=\"post\">hello</div>", "text \"hello\"")]
        [DataRow("<div class=\"post mystery\"></div>", "class=\"post mystery\" on <div>")]
        [DataRow("<div class=\"post\" data-x=\"1\"></div>", "attribute data-x on <div>")]
        [DataRow("<div id=\"p8144079\"></div>", "id=\"p8144079\" on <div>")]
        [DataRow("<a href=\"https://boards.4chan.org/wg/\"></a>", "href=\"https://boards.4chan.org/wg/\" on <a>")]
        [DataRow("<a href=\"{{base}}/wg/thread/7770000001\"></a>", "href=\"{{base}}/wg/thread/7770000001\" on <a>")]
        [DataRow("<span class=\"name\">moot</span>", "text \"moot\"")]
        [DataRow("<img src=\"x.jpg\" title=\"holiday.jpg\">", "title=\"holiday.jpg\" on <img>")]
        [DataRow("<img data-md5=\"AAAAAAAAAAAAAAAAAAAAAA==\">", "data-md5=\"AAAAAAAAAAAAAAAAAAAAAA==\" on <img>")]
        [DataRow("<div></div><!-- posted by someone -->", "text \"<!-- posted by someone -->\"")]
        [DataRow("<script>var a;</script>", "tag <script>")]
        [DataRow("<a class=\"post\" class=\"evil\"></a>", "class=\"evil\" on <a>")]
        [DataRow("leaked<div></div>", "text \"leaked\"")]
        [DataRow("<div></div>\r\nleaked\r\n<div></div>", "text \"leaked\"")]
        [DataRow("<img src=\"{{media}}/src/1234.jpg\">", "src=\"{{media}}/src/1234.jpg\" on <img>")]
        [DataRow("<a target=\"_top\"></a>", "target=\"_top\" on <a>")]
        [DataRow("<DIV CLASS=\"Post\"></DIV>", "class=\"Post\" on <div>")]
        [DataRow("<a href=\"&#104;ttp://x.com/\"></a>", "href=\"&#104;ttp://x.com/\" on <a>")]
        [DataRow("<a href=\"/w8144079/\"></a>", "href=\"/w8144079/\" on <a>")]
        [DataRow("<img src=\"/src/f123456.jpg\">", "src=\"/src/f123456.jpg\" on <img>")]
        [DataRow("<span class=\"name\">name-8144079</span>", "text \"name-8144079\"")]
        [DataRow("<span class=\"postfilename\">file-1.secretword</span>", "text \"file-1.secretword\"")]
        [DataRow("<div></div></div id=\"x\">", "attributes on </div>")]
        [DataRow("<a href=\"/w3/image/AbC/dEf+gHi\"></a>", "href=\"/w3/image/AbC/dEf+gHi\" on <a>")]
        [DataRow("<a href=\"/w3/image/{{md5x_1}}\"></a>", "href=\"/w3/image/{{md5x_1}}\" on <a>")]
        public void SterilityCheckRejectsRealData(string fixture, string expected) {
            CollectionAssert.Contains(SiteFixtures.FindViolations(fixture), expected);
        }

        [TestMethod]
        public void SterilityCheckAcceptsPlaceholders() {
            const string fixture = "<!DOCTYPE html>\n<html><head></head><body><div class=\"thread\" id=\"t7770000001\"><span class=\"name\">name-1</span>" +
                "<span class=\"postertrip\">!trip-2</span><span class=\"deadlink\">&gt;&gt;7770000003</span><span class=\"fileinfo\">File: 10 KB, 64x64, file-4.jpg <!-- {{md5_5}} --></span>" +
                "<a href=\"{{media}}/w6/src/7770000007.jpg\" title=\"file-8.png\" target=\"_blank\"><img src=\"/w6/thumb/7770000009w7.jpg\" data-md5=\"{{md5_10}}\"></a>" +
                "<a href=\"{{base}}/w6/image/{{md5u_10}}#p7770000001\">Anonymous</a><a href=\"/w6/image/{{md5s_10}}\"></a></div></body></html>";

            Assert.IsEmpty(SiteFixtures.FindViolations(fixture));
        }

        private static SiteHelper CreateHelper(string name) {
            Dictionary<string, object> entry = SiteFixtures.Entry(name);
            var helper = (SiteHelper)Activator.CreateInstance(typeof(SiteHelper).Assembly.GetType("JDP." + (string)entry["helper"], true));
            helper.SetURL(SiteFixtures.BaseURL + (string)entry["pagePath"]);
            helper.SetHTMLParser(new HTMLParser(SiteFixtures.Substitute(SiteFixtures.ReadFixture(name))));
            return helper;
        }
    }
}
