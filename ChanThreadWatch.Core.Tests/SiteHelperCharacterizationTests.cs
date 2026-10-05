using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Characterization tests: each one pins the complete current output of a site helper
    // against a golden file in Fixtures/expected. On a mismatch the actual output is written
    // next to the test assembly as <name>.actual.txt so the difference can be inspected.
    [TestClass]
    public class SiteHelperCharacterizationTests {
        private const string FourChanURL = "https://boards.4chan.org/wg/thread/8143532";
        private const string InfinitechanURL = "https://8ch.net/tech/res/100.html";

        // Linux and macOS keep the '?' of the image file name "1005.webm?x=1&y=2" (G4), so they compare against
        // generic-images.unix.txt, which differs from the Windows file only in that name
        [TestMethod]
        public void GenericGetImages() {
            AssertGetImages("http://example.com/b/res/1.html", "generic-thread.html", "generic-images");
        }

        // Linux and macOS keep the '<' and '>' of "sun <rise> & set.jpg" (G4), so they compare against
        // 4chan-edge-images.unix.txt, which differs from the Windows file only in that name
        [TestMethod]
        public void FourChanGetImagesEdgeCases() {
            AssertGetImages(FourChanURL, "4chan-edge-cases.html", "4chan-edge-images");
        }

        [TestMethod]
        public void FourChanGetImagesThread() {
            AssertGetImages(FourChanURL, "4chan-thread.html", "4chan-thread-images");
        }

        [TestMethod]
        public void InfinitechanGetImages() {
            AssertGetImages(InfinitechanURL, "8ch-thread.html", "8ch-images");
        }

        [TestMethod]
        public void FuukaGetImages() {
            AssertGetImages("https://warosu.org/g/thread/123", "fuuka-thread.html", "fuuka-images");
        }

        [TestMethod]
        public void FoolFuukaGetImages() {
            AssertGetImages("https://archive.4plebs.org/tg/thread/123/", "foolfuuka-thread.html", "foolfuuka-images");
        }

        [TestMethod]
        public void LynxChanGetImages() {
            AssertGetImages("https://endchan.org/b/res/5.html", "lynxchan-thread.html", "lynxchan-images");
        }

        [TestMethod]
        public void FourChanGetCrossLinks() {
            AssertGetCrossLinks(FourChanURL, "4chan-crosslinks.html", "4chan-crosslinks");
        }

        [TestMethod]
        public void InfinitechanGetCrossLinks() {
            AssertGetCrossLinks(InfinitechanURL, "8ch-thread.html", "8ch-crosslinks");
        }

        [TestMethod]
        public void FourChanResurrectDeadPosts() {
            AssertResurrectDeadPosts(FourChanURL, "4chan-resurrect-previous.html", "4chan-resurrect-current.html", "4chan-resurrect");
        }

        [TestMethod]
        public void InfinitechanResurrectDeadPosts() {
            AssertResurrectDeadPosts(InfinitechanURL, "8ch-resurrect-previous.html", "8ch-resurrect-current.html", "8ch-resurrect");
        }

        [TestMethod]
        [DataRow(FourChanURL, "4chan-resurrect-current.html")]
        [DataRow(InfinitechanURL, "8ch-resurrect-current.html")]
        public void ResurrectDeadPostsWithoutPreviousParserKeepsParser(string url, string fixture) {
            SiteHelper helper = CreateHelper(url, fixture);
            HTMLParser parser = helper.GetHTMLParser();
            var replaces = new List<ReplaceInfo>();

            helper.ResurrectDeadPosts(null, replaces);

            Assert.AreSame(parser, helper.GetHTMLParser());
            Assert.IsEmpty(replaces);
        }

        private static void AssertGetImages(string url, string fixture, string expectedName) {
            SiteHelper helper = CreateHelper(url, fixture);
            var replaces = new List<ReplaceInfo>();
            var thumbs = new List<ThumbnailInfo>();
            List<ImageInfo> images = helper.GetImages(replaces, thumbs);

            var sb = new StringBuilder();
            DescribeImages(sb, images, thumbs);
            DescribeReplaces(sb, helper.GetHTMLParser(), replaces);
            AssertMatchesGolden(expectedName, sb.ToString());

            // A null replace list must not change the images or thumbnails.
            SiteHelper helperWithoutReplaces = CreateHelper(url, fixture);
            var thumbsWithoutReplaces = new List<ThumbnailInfo>();
            List<ImageInfo> imagesWithoutReplaces = helperWithoutReplaces.GetImages(null, thumbsWithoutReplaces);
            var withReplaces = new StringBuilder();
            DescribeImages(withReplaces, images, thumbs);
            var withoutReplaces = new StringBuilder();
            DescribeImages(withoutReplaces, imagesWithoutReplaces, thumbsWithoutReplaces);
            Assert.AreEqual(withReplaces.ToString(), withoutReplaces.ToString());
        }

        private static void AssertGetCrossLinks(string url, string fixture, string expectedName) {
            var sb = new StringBuilder();
            foreach (bool interBoardAutoFollow in new[] { false, true }) {
                SiteHelper helper = CreateHelper(url, fixture);
                var replaces = new List<ReplaceInfo>();
                HashSet<string> crossLinks = helper.GetCrossLinks(replaces, interBoardAutoFollow);

                sb.Append("interBoardAutoFollow=").Append(interBoardAutoFollow).Append('\n');
                DescribeCrossLinks(sb, crossLinks);
                DescribeReplaces(sb, helper.GetHTMLParser(), replaces);

                HashSet<string> crossLinksWithoutReplaces = CreateHelper(url, fixture).GetCrossLinks(null, interBoardAutoFollow);
                CollectionAssert.AreEquivalent(crossLinks.ToList(), crossLinksWithoutReplaces.ToList());
            }
            AssertMatchesGolden(expectedName, sb.ToString());
        }

        private static void AssertResurrectDeadPosts(string url, string previousFixture, string currentFixture, string expectedName) {
            SiteHelper helper = CreateHelper(url, currentFixture);
            var replaces = new List<ReplaceInfo>();
            helper.ResurrectDeadPosts(LoadFixture(previousFixture), replaces);

            var sb = new StringBuilder();
            sb.Append("html:\n").Append(helper.GetHTMLParser().PreprocessedHTML).Append('\n');
            DescribeReplaces(sb, helper.GetHTMLParser(), replaces);
            AssertMatchesGolden(expectedName, sb.ToString());

            // A null replace list must still rewrite the page the same way.
            SiteHelper helperWithoutReplaces = CreateHelper(url, currentFixture);
            helperWithoutReplaces.ResurrectDeadPosts(LoadFixture(previousFixture), null);
            Assert.AreEqual(helper.GetHTMLParser().PreprocessedHTML, helperWithoutReplaces.GetHTMLParser().PreprocessedHTML);
        }

        private static void DescribeImages(StringBuilder sb, List<ImageInfo> images, List<ThumbnailInfo> thumbs) {
            sb.Append("images:\n");
            foreach (ImageInfo image in images) {
                sb.Append("  ").Append(String.Join(" | ", new[] {
                    image.URL, image.Referer, Show(image.OriginalFileName), Show(image.Poster),
                    image.HashType.ToString(), image.Hash == null ? "(null)" : BitConverter.ToString(image.Hash)
                })).Append('\n');
            }
            sb.Append("thumbnails:\n");
            foreach (ThumbnailInfo thumb in thumbs) {
                sb.Append("  ").Append(thumb.URL).Append(" | ").Append(thumb.Referer).Append('\n');
            }
        }

        private static void DescribeCrossLinks(StringBuilder sb, HashSet<string> crossLinks) {
            sb.Append("crossLinks:\n");
            foreach (string crossLink in crossLinks.OrderBy(c => c, StringComparer.Ordinal)) {
                sb.Append("  ").Append(crossLink).Append('\n');
            }
        }

        private static void DescribeReplaces(StringBuilder sb, HTMLParser parser, List<ReplaceInfo> replaces) {
            sb.Append("replaces:\n");
            foreach (ReplaceInfo replace in replaces) {
                sb.Append("  ").Append(String.Join(" | ", new[] {
                    replace.Offset.ToString(), replace.Length.ToString(), replace.Type.ToString(),
                    Show(replace.Tag), Show(replace.Value), parser.PreprocessedHTML.Substring(replace.Offset, replace.Length)
                })).Append('\n');
            }
        }

        private static string Show(string value) {
            return value == null ? "(null)" : "[" + value + "]";
        }

        private static void AssertMatchesGolden(string name, string actual) {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "expected");
            string expected = File.ReadAllText(GoldenPath(directory, name)).Replace("\r\n", "\n");
            if (expected == actual) return;
            string actualPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".actual.txt");
            File.WriteAllText(actualPath, actual);
            Assert.Fail("Output differs from Fixtures/expected/" + name + ".txt. Actual output: " + actualPath);
        }

        // Off Windows a "<name>.unix.txt" file, where there is one, holds the values that differ by OS (G4)
        private static string GoldenPath(string directory, string name) {
            string unixPath = Path.Combine(directory, name + ".unix.txt");
            return !OperatingSystem.IsWindows() && File.Exists(unixPath) ? unixPath : Path.Combine(directory, name + ".txt");
        }

        private static SiteHelper CreateHelper(string url, string fixture) {
            SiteHelper helper = SiteHelpers.GetInstance(new Uri(url).Host);
            helper.SetURL(url);
            helper.SetHTMLParser(LoadFixture(fixture));
            return helper;
        }

        private static HTMLParser LoadFixture(string name) {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);
            return new HTMLParser(File.ReadAllText(path));
        }
    }
}
