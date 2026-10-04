using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class FourChanSiteHelperTests {
        private const string SlugURL = "https://boards.4chan.org/wg/thread/8143532/japan-papes-continued";
        private const string PlainURL = "https://boards.4chan.org/wg/thread/8143532";

        [TestMethod]
        [DataRow("boards.4chan.org", typeof(FourChanSiteHelper))]
        [DataRow("BOARDS.4CHAN.ORG", typeof(FourChanSiteHelper))]
        [DataRow("boards.4channel.org", typeof(FourChanSiteHelper))]
        [DataRow("archive.4plebs.org", typeof(FoolFuukaSiteHelper))]
        [DataRow("endchan.org", typeof(LynxChanSiteHelper))]
        [DataRow("krautchan.net", typeof(SiteHelper))]
        [DataRow("example.com", typeof(SiteHelper))]
        public void GetInstanceMapsHostToHelper(string host, Type expected) {
            Assert.AreEqual(expected, SiteHelpers.GetInstance(host).GetType());
        }

        [TestMethod]
        public void ParsesThreadURLWithSlug() {
            SiteHelper helper = CreateHelper(SlugURL);

            Assert.IsTrue(helper.HasSlug());
            Assert.AreEqual("8143532", helper.GetThreadID());
            Assert.AreEqual("wg", helper.GetBoardName());
            Assert.AreEqual("4chan/wg/8143532", helper.GetPageID());
        }

        [TestMethod]
        public void ParsesThreadURLWithoutSlug() {
            SiteHelper helper = CreateHelper(PlainURL);

            Assert.IsFalse(helper.HasSlug());
            Assert.AreEqual("8143532", helper.GetThreadID());
            Assert.AreEqual("wg", helper.GetBoardName());
            Assert.AreEqual("4chan/wg/8143532", helper.GetPageID());
        }

        [TestMethod]
        public void GetImagesExtractsFilesFromThreadFixture() {
            SiteHelper helper = CreateHelper(PlainURL);
            HTMLParser parser = LoadFixture("4chan-thread.html");
            helper.SetHTMLParser(parser);
            var replaces = new List<ReplaceInfo>();
            var thumbs = new List<ThumbnailInfo>();

            List<ImageInfo> images = helper.GetImages(replaces, thumbs);

            CollectionAssert.AreEqual(
                new[] { "1700000000001.jpg", "1700000000002.png", "1700000000003.png", "1700000000004.gif" },
                images.Select(i => i.FileName).ToArray());
            Assert.AreEqual("https://i.4cdn.org/wg/1700000000001.jpg", images[0].URL);
            Assert.AreEqual(PlainURL, images[0].Referer);
            CollectionAssert.AreEqual(
                new[] { "mountain & lake.jpg", "a very long file name.png", "secret.png", "hidden.gif" },
                images.Select(i => i.OriginalFileName).ToArray());
            CollectionAssert.AreEqual(
                new[] { "", "Bob!Trip", "AbC123", "" },
                images.Select(i => i.Poster).ToArray());

            Assert.AreEqual(HashType.MD5, images[0].HashType);
            CollectionAssert.AreEqual(System.Linq.Enumerable.Range(1, 16).Select(b => (byte)b).ToArray(), images[0].Hash);
            Assert.AreEqual(HashType.None, images[3].HashType);
            Assert.IsNull(images[3].Hash);
        }

        [TestMethod]
        public void GetImagesAddsOneSpoilerThumbnail() {
            SiteHelper helper = CreateHelper(PlainURL);
            helper.SetHTMLParser(LoadFixture("4chan-thread.html"));
            var thumbs = new List<ThumbnailInfo>();

            helper.GetImages(null, thumbs);

            CollectionAssert.AreEqual(
                new[] {
                    "https://i.4cdn.org/wg/1700000000001s.jpg",
                    "https://i.4cdn.org/wg/1700000000002s.jpg",
                    "https://s.4cdn.org/image/spoiler-wg.png"
                },
                thumbs.Select(t => t.URL).ToArray());
        }

        [TestMethod]
        public void GetImagesRecordsReplacementsAtAttributeOffsets() {
            SiteHelper helper = CreateHelper(PlainURL);
            HTMLParser parser = LoadFixture("4chan-thread.html");
            helper.SetHTMLParser(parser);
            var replaces = new List<ReplaceInfo>();

            helper.GetImages(replaces, new List<ThumbnailInfo>());

            Assert.HasCount(12, replaces);
            foreach (ReplaceInfo replace in replaces) {
                string attributeText = parser.PreprocessedHTML.Substring(replace.Offset, replace.Length);
                string expectedPrefix = replace.Type == ReplaceType.ImageSrc ? "src=" : "href=";
                Assert.StartsWith(expectedPrefix, attributeText);
                StringAssert.EndsWith(attributeText, "/" + replace.Tag + "\"");
            }
            Assert.AreEqual(8, replaces.Count(r => r.Type == ReplaceType.ImageLinkHref));
            Assert.AreEqual(4, replaces.Count(r => r.Type == ReplaceType.ImageSrc));
        }

        private static SiteHelper CreateHelper(string url) {
            SiteHelper helper = SiteHelpers.GetInstance(new Uri(url).Host);
            helper.SetURL(url);
            return helper;
        }

        private static HTMLParser LoadFixture(string name) {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);
            return new HTMLParser(File.ReadAllText(path));
        }
    }
}
