using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Malformed or unexpected markup must make the site helpers skip the affected item, never
    // throw or return entries without a URL.
    [TestClass]
    public class SiteHelperRobustnessTests {
        private const string FourChanURL = "https://boards.4chan.org/wg/thread/100";
        private const string InfinitechanURL = "https://8ch.net/tech/res/100.html";
        private const string FuukaURL = "https://warosu.org/g/thread/123";
        private const string FoolFuukaURL = "https://archive.4plebs.org/tg/thread/123/";
        private const string LynxChanURL = "https://endchan.org/b/res/5.html";

        // General.GetAbsoluteURL rejects this URL and returns null.
        private const string BadURL = "http://[bad/src/1.jpg";

        private const string ErrorPage = "<html><head><title>Error</title></head><body><h1>Checking your browser</h1><div class=\"post\">not a post</div></body></html>";

        [TestMethod]
        public void BadURLIsRejectedByGetAbsoluteURL() {
            Assert.IsNull(General.GetAbsoluteURL(FourChanURL, BadURL));
        }

        // R8 / B14
        [TestMethod]
        public void FourChanGetImagesSkipsUnresolvableImageURL() {
            string html = FourChanPost("p1", BadURL) + FourChanPost("p2", "//i.4cdn.org/wg/2.jpg");

            AssertOnlyImage(FourChanURL, html, "https://i.4cdn.org/wg/2.jpg");
        }

        // R8 / B14
        [TestMethod]
        public void InfinitechanGetImagesSkipsUnresolvableImageURL() {
            string html = "<div class=\"thread\">" + InfinitechanReply("reply_1", InfinitechanFiles(BadURL, "/file_store/2.jpg"), true) + "</div>";

            AssertOnlyImage(InfinitechanURL, html, "https://8ch.net/file_store/2.jpg");
        }

        // R8 / B14
        [TestMethod]
        public void FuukaGetImagesSkipsUnresolvableImageURL() {
            string html = FuukaPost("p1", BadURL, "a.jpg") + FuukaPost("p2", "https://i.warosu.org/data/g/img/2.jpg", "b.jpg");

            AssertOnlyImage(FuukaURL, html, "https://i.warosu.org/data/g/img/2.jpg");
        }

        // R8 / B14
        [TestMethod]
        public void FoolFuukaGetImagesSkipsUnresolvableImageURL() {
            string html = FoolFuukaPost("1", BadURL) + FoolFuukaPost("2", "https://i.4pcdn.org/tg/2.jpg");

            AssertOnlyImage(FoolFuukaURL, html, "https://i.4pcdn.org/tg/2.jpg");
        }

        // R8 / B14
        [TestMethod]
        public void LynxChanGetImagesSkipsUnresolvableImageURL() {
            string html = "<div class=\"postCell\" id=\"6\"><div class=\"innerPost\"><a class=\"linkName\">Heidi</a><div class=\"panelUploads\">" +
                LynxChanUpload(BadURL) + LynxChanUpload("/.media/2.png") + "</div></div></div>";

            AssertOnlyImage(LynxChanURL, html, "https://endchan.org/.media/2.png");
        }

        // R8 / B14
        [TestMethod]
        public void InfinitechanGetImagesSkipsPostWithoutFilesDiv() {
            string html = "<div class=\"thread\">" +
                InfinitechanReply("reply_1", String.Empty, true) +
                InfinitechanReply("reply_2", InfinitechanFiles("/file_store/2.jpg"), true) + "</div>";

            AssertOnlyImage(InfinitechanURL, html, "https://8ch.net/file_store/2.jpg");
        }

        // R8 / B14
        [TestMethod]
        public void InfinitechanGetImagesWithoutNameSpanHasNoPoster() {
            string html = "<div class=\"thread\">" + InfinitechanReply("reply_1", InfinitechanFiles("/file_store/2.jpg"), false) + "</div>";

            List<ImageInfo> images = GetImages(InfinitechanURL, html);

            Assert.HasCount(1, images);
            Assert.AreEqual(String.Empty, images[0].Poster);
        }

        // R8 / B14
        [TestMethod]
        public void LynxChanGetImagesSkipsPostWithoutPanelUploads() {
            string html = "<div class=\"postCell\" id=\"6\"><div class=\"innerPost\"><a class=\"linkName\">Heidi</a></div></div>" +
                "<div class=\"postCell\" id=\"7\"><div class=\"innerPost\"><div class=\"panelUploads\">" + LynxChanUpload("/.media/2.png") + "</div></div></div>";

            AssertOnlyImage(LynxChanURL, html, "https://endchan.org/.media/2.png");
        }

        // R8 / B14
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FourChanGetCrossLinksSkipsUnresolvableQuoteLink(bool withReplaces) {
            string html = "<blockquote class=\"postMessage\"><a href=\"//[bad/thread/1#p2\" class=\"quotelink\">x</a>" +
                "<a href=\"/g/thread/5#p6\" class=\"quotelink\">y</a></blockquote>";
            SiteHelper helper = CreateHelper(FourChanURL, html);
            List<ReplaceInfo> replaces = withReplaces ? new List<ReplaceInfo>() : null;

            HashSet<string> crossLinks = helper.GetCrossLinks(replaces, true);

            CollectionAssert.AreEqual(new[] { "https://boards.4chan.org/g/thread/5" }, crossLinks.ToArray());
            if (withReplaces) {
                CollectionAssert.AreEqual(new[] { "4chan/g/5" }, replaces.Select(r => r.Tag).ToArray());
            }
        }

        // R8 / B14: 8ch quote links always resolve against a valid page URL, so an unresolvable
        // page URL is the only way to make GetAbsoluteURL return null here.
        [TestMethod]
        public void InfinitechanGetCrossLinksSkipsUnresolvableQuoteLink() {
            string html = "<div class=\"body\"><a href=\"/g/res/300.html#301\">x</a></div>";
            SiteHelper helper = CreateHelper("http://[8ch.net/tech/res/100.html", html);

            HashSet<string> crossLinks = helper.GetCrossLinks(null, true);

            Assert.IsEmpty(crossLinks);
        }

        // B12
        [TestMethod]
        public void InfinitechanGetCrossLinksSkipsLinksToSameThread() {
            string html = "<div class=\"body\"><a href=\"/tech/res/100.html#101\">same</a><a href=\"/tech/res/200.html#201\">other</a></div>";
            SiteHelper helper = CreateHelper(InfinitechanURL, html);
            var replaces = new List<ReplaceInfo>();

            HashSet<string> crossLinks = helper.GetCrossLinks(replaces, false);

            CollectionAssert.AreEqual(new[] { "https://8ch.net/tech/res/200.html" }, crossLinks.ToArray());
            CollectionAssert.AreEqual(new[] { "8ch/tech/200" }, replaces.Select(r => r.Tag).ToArray());
        }

        // B12: with no QuoteLinkHref replace, General.AddOtherReplaces turns a same-thread link into
        // its fragment. This mirrors ThreadWatcher.Process for the root thread, whose own page is
        // not in DescendantThreads, so ApplyThreadLinkReplace leaves the replace value unchanged.
        [TestMethod]
        public void InfinitechanSavedPageTurnsSameThreadLinksIntoInPageAnchors() {
            string html = "<div class=\"body\"><a href=\"/tech/res/100.html#101\">same</a><a href=\"/tech/res/200.html#201\">other</a></div>";
            SiteHelper helper = CreateHelper(InfinitechanURL, html);
            var replaces = new List<ReplaceInfo>();

            helper.GetCrossLinks(replaces, false);
            General.AddOtherReplaces(helper.GetHTMLParser(), InfinitechanURL, replaces);
            var savedPage = new StringWriter();
            General.WriteReplacedString(helper.GetHTMLParser().PreprocessedHTML, replaces, savedPage);

            StringAssert.Contains(savedPage.ToString(), "<a href=\"#101\">same</a>");
        }

        // A cross link to a thread that is not followed keeps its QuoteLinkHref replace with the
        // default value, because ThreadWatcher.ApplyThreadLinkReplace finds no descendant thread.
        // The saved page must then keep a well-formed link to the live page.
        [TestMethod]
        [DataRow(FourChanURL, "<blockquote class=\"postMessage\"><a href=\"/g/thread/123?a=1&amp;b=2#p124\" class=\"quotelink\">x</a></blockquote>",
            "<a href=\"https://boards.4chan.org/g/thread/123?a=1&amp;b=2#p124\" class=\"quotelink\">x</a>")]
        [DataRow(InfinitechanURL, "<div class=\"body\"><a href=\"/g/res/300.html#301\">x</a></div>",
            "<a href=\"https://8ch.net/g/res/300.html#301\">x</a>")]
        public void SavedPageKeepsUnfollowedCrossLinkAsLiveLink(string url, string html, string expectedLink) {
            SiteHelper helper = CreateHelper(url, html);
            var replaces = new List<ReplaceInfo>();

            helper.GetCrossLinks(replaces, true);
            General.AddOtherReplaces(helper.GetHTMLParser(), url, replaces);
            var savedPage = new StringWriter();
            General.WriteReplacedString(helper.GetHTMLParser().PreprocessedHTML, replaces, savedPage);

            Assert.IsTrue(replaces.Exists(r => r.Type == ReplaceType.QuoteLinkHref));
            StringAssert.Contains(savedPage.ToString(), expectedLink);
        }

        // B13
        [TestMethod]
        public void GenericGetImagesUsesPageAsReferer() {
            const string pageURL = "http://example.com/b/res/1.html";
            string html = "<a href=\"/b/src/1.jpg\">a</a><a href=\"https://example.com/b/src/2.jpg\">b</a><a href=\"/redirect/src/http://other.example.org/img/3.jpg\">c</a>";

            List<ImageInfo> images = GetImages(pageURL, html);

            CollectionAssert.AreEqual(
                new[] {
                    "http://example.com/b/src/1.jpg " + pageURL,
                    "https://example.com/b/src/2.jpg " + pageURL,
                    "http://other.example.org/img/3.jpg http://example.com/redirect/src/http://other.example.org/img/3.jpg"
                },
                images.Select(i => i.URL + " " + i.Referer).ToArray());
        }

        // B15
        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FourChanGetCrossLinksSkipsDeadLinksWithoutPostNumber(bool withReplaces) {
            string html = "<blockquote class=\"postMessage\"><span class=\"deadlink\">&gt;</span><span class=\"deadlink\"></span>" +
                "<span class=\"deadlink\">&gt;&gt;&gt;/g</span><span class=\"deadlink\">&gt;&gt;7</span></blockquote>";
            SiteHelper helper = CreateHelper(FourChanURL, html);
            List<ReplaceInfo> replaces = withReplaces ? new List<ReplaceInfo>() : null;

            helper.GetCrossLinks(replaces, true);

            if (withReplaces) {
                CollectionAssert.AreEqual(new[] { "4chan/wg/7" }, replaces.Select(r => r.Tag).ToArray());
            }
        }

        // B15 (resurrect side): a one-character dead link next to a resurrected post
        [TestMethod]
        public void FourChanResurrectDeadPostsSkipsShortDeadLink() {
            string previous = FourChanThread(FourChanContainer("pc1", "first") + FourChanContainer("pc2", "deleted"));
            string current = FourChanThread(FourChanContainer("pc1", "<span class=\"deadlink\">&gt;</span>"));
            SiteHelper helper = CreateHelper(FourChanURL, current);

            helper.ResurrectDeadPosts(new HTMLParser(previous), new List<ReplaceInfo>());

            StringAssert.Contains(helper.GetHTMLParser().PreprocessedHTML, "id=\"pc2\"");
        }

        // B16
        [TestMethod]
        public void FourChanResurrectDeadPostsToleratesDuplicateAndMissingIDs() {
            string previous = FourChanThread(
                FourChanContainer("pc1", "first") + FourChanContainer("pc2", "deleted") + FourChanContainer("pc2", "duplicate deleted") +
                FourChanContainer(null, "no id"));
            string current = FourChanThread(
                FourChanContainer("pc1", "first") + FourChanContainer("pc1", "duplicate") + FourChanContainer(null, "no id"));
            SiteHelper helper = CreateHelper(FourChanURL, current);

            helper.ResurrectDeadPosts(new HTMLParser(previous), new List<ReplaceInfo>());

            string html = helper.GetHTMLParser().PreprocessedHTML;
            StringAssert.Contains(html, "deleted</blockquote>");
            Assert.DoesNotContain("duplicate deleted", html);
        }

        // Consecutive deleted posts after the same surviving post are all inserted at one offset.
        // More than 16 of them makes List.Sort leave insertion sort, so only a stable sort keeps
        // them in thread order.
        [TestMethod]
        public void FourChanResurrectDeadPostsKeepsOrderOfManyConsecutiveDeletedPosts() {
            string[] deletedIDs = System.Linq.Enumerable.Range(2, 40).Select(n => "pc" + n).ToArray();
            string previous = FourChanThread(FourChanContainer("pc1", "first") + String.Concat(deletedIDs.Select(id => FourChanContainer(id, id))));
            SiteHelper helper = CreateHelper(FourChanURL, FourChanThread(FourChanContainer("pc1", "first")));
            var replaces = new List<ReplaceInfo>();

            helper.ResurrectDeadPosts(new HTMLParser(previous), replaces);
            General.AddOtherReplaces(helper.GetHTMLParser(), FourChanURL, replaces);
            var savedPage = new StringWriter();
            General.WriteReplacedString(helper.GetHTMLParser().PreprocessedHTML, replaces, savedPage);

            string html = savedPage.ToString();
            int[] positions = deletedIDs.Select(id => html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal)).ToArray();
            CollectionAssert.DoesNotContain(positions, -1);
            CollectionAssert.AreEqual(positions.OrderBy(p => p).ToArray(), positions, "resurrected posts are out of thread order");
        }

        // B16
        [TestMethod]
        public void InfinitechanResurrectDeadPostsToleratesDuplicateAndMissingIDs() {
            string previous = InfinitechanThread(
                InfinitechanPost("op_1", "first") + InfinitechanPost("reply_2", "deleted") + InfinitechanPost("reply_2", "duplicate deleted") +
                InfinitechanPost(null, "no id"));
            string current = InfinitechanThread(
                InfinitechanPost("op_1", "first") + InfinitechanPost("op_1", "duplicate") + InfinitechanPost(null, "no id"));
            SiteHelper helper = CreateHelper(InfinitechanURL, current);

            helper.ResurrectDeadPosts(new HTMLParser(previous), new List<ReplaceInfo>());

            string html = helper.GetHTMLParser().PreprocessedHTML;
            StringAssert.Contains(html, "deleted</div>");
            Assert.DoesNotContain("duplicate deleted", html);
        }

        // B16 / R7: an error page served instead of the thread has no thread div
        [TestMethod]
        [DataRow(FourChanURL, true)]
        [DataRow(InfinitechanURL, false)]
        public void ResurrectDeadPostsLeavesErrorPageUnchanged(string url, bool isFourChan) {
            string previous = isFourChan ?
                FourChanThread(FourChanContainer("pc1", "first")) :
                InfinitechanThread(InfinitechanPost("op_1", "first"));
            SiteHelper helper = CreateHelper(url, ErrorPage);
            HTMLParser parser = helper.GetHTMLParser();
            var replaces = new List<ReplaceInfo>();

            helper.ResurrectDeadPosts(new HTMLParser(previous), replaces);

            Assert.AreSame(parser, helper.GetHTMLParser());
            Assert.IsEmpty(replaces);
        }

        // B17
        [TestMethod]
        public void FuukaGetImagesSkipsImageWithInvalidMD5() {
            string html = FuukaPost("p1", "https://i.warosu.org/data/g/img/1.jpg", "bad.jpg <!-- %%% -->") +
                FuukaPost("p2", "https://i.warosu.org/data/g/img/2.jpg", "good.jpg <!-- AQIDBAUGBwgJCgsMDQ4PEA== -->");

            List<ImageInfo> images = AssertOnlyImage(FuukaURL, html, "https://i.warosu.org/data/g/img/2.jpg");

            Assert.AreEqual(HashType.MD5, images[0].HashType);
            Assert.IsNotNull(images[0].Hash);
        }

        // B26: a dead post without a checkbox is still resurrected, with the marker after its start tag
        [TestMethod]
        public void FourChanResurrectDeadPostsMarksPostWithoutCheckbox() {
            string previous = FourChanThread(FourChanContainer("pc1", "first") +
                "<div class=\"postContainer replyContainer\" id=\"pc2\"><div class=\"post reply\"><blockquote class=\"postMessage\">deleted</blockquote></div></div>");
            string current = FourChanThread(FourChanContainer("pc1", "first"));
            SiteHelper helper = CreateHelper(FourChanURL, current);

            helper.ResurrectDeadPosts(new HTMLParser(previous), new List<ReplaceInfo>());

            StringAssert.Contains(helper.GetHTMLParser().PreprocessedHTML,
                "<div class=\"postContainer replyContainer\" id=\"pc2\"><strong style=\"color: #FF0000\">[Deleted]</strong><div class=\"post reply\">");
        }

        // B26
        [TestMethod]
        public void InfinitechanResurrectDeadPostsMarksPostWithoutCheckbox() {
            string previous = InfinitechanThread(InfinitechanPost("op_1", "first") +
                "<div class=\"post reply\" id=\"reply_2\"><p class=\"intro\"><span class=\"name\">Anonymous</span></p><div class=\"body\">deleted</div></div>");
            string current = InfinitechanThread(InfinitechanPost("op_1", "first"));
            SiteHelper helper = CreateHelper(InfinitechanURL, current);

            helper.ResurrectDeadPosts(new HTMLParser(previous), new List<ReplaceInfo>());

            StringAssert.Contains(helper.GetHTMLParser().PreprocessedHTML,
                "<div class=\"post reply\" id=\"reply_2\"><strong style=\"color: #FF0000\">[Deleted]</strong> <p class=\"intro\">");
        }

        // B27: a URL without path segments (e.g. a saved page path) gives an empty thread ID
        // instead of throwing, while a short URL such as a board URL keeps its last segment, so
        // that different boards still get different page IDs and folder names
        [TestMethod]
        [DataRow(@"C:\Threads\100.html", "")]
        [DataRow("https://boards.4chan.org/wg/", "wg")]
        public void FourChanThreadIDOfURLWithTooFewSegments(string url, string expected) {
            SiteHelper helper = SiteHelpers.GetInstance("boards.4chan.org");
            helper.SetURL(url);

            Assert.AreEqual(expected, helper.GetThreadID());
            Assert.AreEqual(expected, helper.GetThreadName());
        }

        // B28: a post that is a td with a div of the same id inside it has its image read once
        [TestMethod]
        public void FuukaGetImagesReadsPostInsideSameIDCellOnce() {
            const string imageURL = "https://i.warosu.org/data/g/img/1.jpg";
            string html = "<table><tr><td id=\"p1\">" + FuukaPost("p1", imageURL, "a.jpg") + "</td></tr></table>";
            var replaces = new List<ReplaceInfo>();
            var thumbs = new List<ThumbnailInfo>();

            List<ImageInfo> images = CreateHelper(FuukaURL, html).GetImages(replaces, thumbs);

            CollectionAssert.AreEqual(new[] { imageURL }, images.Select(i => i.URL).ToArray());
            Assert.HasCount(1, thumbs);
            Assert.HasCount(2, replaces);
        }

        // Without an MD5 comment, the MD5 is read from the "same image" link (/<board>/image/<md5>).
        // A standard base64 MD5 can contain "/" and "+", and must not be cut at its last "/".
        // Each form here is the same 16 bytes of 0xFB.
        [TestMethod]
        [DataRow("+/v7+/v7+/v7+/v7+/v7+w")]
        [DataRow("+/v7+/v7+/v7+/v7+/v7+w==")]
        [DataRow("-_v7-_v7-_v7-_v7-_v7-w")]
        [DataRow("%2B%2Fv7%2B%2Fv7%2B%2Fv7%2B%2Fv7%2B%2Fv7%2Bw%3D%3D")]
        public void FuukaGetImagesReadsMD5FromSameImageLink(string linkMD5) {
            const string imageURL = "https://i.warosu.org/data/g/img/1.jpg";
            string html = FuukaPostWithSameImageLink("p1", imageURL, "/g/image/" + linkMD5);

            List<ImageInfo> images = AssertOnlyImage(FuukaURL, html, imageURL);

            Assert.AreEqual(HashType.MD5, images[0].HashType);
            CollectionAssert.AreEqual(System.Linq.Enumerable.Repeat((byte)0xFB, 16).ToArray(), images[0].Hash);
        }

        [TestMethod]
        public void FuukaGetImagesWithoutMD5CommentOrSameImageLinkHasNoHash() {
            const string imageURL = "https://i.warosu.org/data/g/img/1.jpg";
            string html = FuukaPostWithSameImageLink("p1", imageURL, "/g/thread/123");

            List<ImageInfo> images = AssertOnlyImage(FuukaURL, html, imageURL);

            Assert.AreEqual(HashType.None, images[0].HashType);
            Assert.IsNull(images[0].Hash);
        }

        // Every link in the post to the full image is rewritten to the local file, including the
        // plain links desuarchive has in post_file_controls and the post header; links to other
        // URLs (search, a different image) are kept
        [TestMethod]
        public void FoolFuukaGetImagesRewritesEveryLinkToTheFullImage() {
            const string imageURL = "https://i.4pcdn.org/tg/1.jpg";
            string html = "<article class=\"post has_image\" id=\"1\">" +
                "<div class=\"post_file\"><span class=\"post_file_controls\"><a href=\"/tg/search/image/AQIDBAUGBwgJCgsMDQ4PEA/\"></a><a href=\"" + imageURL + "\"></a></span>" +
                "<a href=\"" + imageURL + "\" class=\"post_file_filename\">a.jpg</a></div>" +
                "<div><a href=\"" + imageURL + "\" class=\"thread_image_link\"><img src=\"https://i.4pcdn.org/tg/1s.jpg\" data-md5=\"AQIDBAUGBwgJCgsMDQ4PEA==\"></a></div>" +
                "<header><div><a href=\"https://i.4pcdn.org/tg/2.jpg\"></a><a href=\"//i.4pcdn.org/tg/1.jpg\"></a></div>" +
                "<span class=\"post_poster_data\"><span class=\"post_author\">Frank</span></span></header></article>";
            SiteHelper helper = CreateHelper(FoolFuukaURL, html);
            var replaces = new List<ReplaceInfo>();

            List<ImageInfo> images = helper.GetImages(replaces, new List<ThumbnailInfo>());

            Assert.HasCount(1, images);
            string[] rewrittenLinks = replaces.Where(r => r.Type == ReplaceType.ImageLinkHref)
                .Select(r => helper.GetHTMLParser().PreprocessedHTML.Substring(r.Offset, r.Length)).ToArray();
            CollectionAssert.AreEquivalent(new[] {
                "href=\"" + imageURL + "\"", "href=\"" + imageURL + "\"", "href=\"" + imageURL + "\"", "href=\"//i.4pcdn.org/tg/1.jpg\""
            }, rewrittenLinks);
            Assert.IsTrue(replaces.Where(r => r.Type == ReplaceType.ImageLinkHref).All(r => r.Tag == images[0].FileName));
        }

        // An IP address host is used whole for the site name (and so the folder name), instead
        // of its next-to-last number; domain names keep their second-level name
        [TestMethod]
        [DataRow("http://127.0.0.1:8080/b/res/123.html", "127.0.0.1")]
        [DataRow("http://[::1]:8080/b/res/123.html", "[--1]")]
        [DataRow("http://[fe80::1]/b/res/123.html", "[fe80--1]")]
        [DataRow("https://boards.example.com/b/res/123.html", "example")]
        public void GenericSiteNameOfIPAddressHostIsTheWholeHost(string url, string expected) {
            SiteHelper helper = new SiteHelper();
            helper.SetURL(url);

            Assert.AreEqual(expected, helper.GetSiteName());
            Assert.AreEqual(expected + "/b/123", helper.GetPageID());
        }

        private static List<ImageInfo> AssertOnlyImage(string url, string html, string expectedImageURL) {
            List<ImageInfo> images = GetImages(url, html);
            CollectionAssert.AreEqual(new[] { expectedImageURL }, images.Select(i => i.URL).ToArray());
            return images;
        }

        private static List<ImageInfo> GetImages(string url, string html) {
            List<ImageInfo> images = CreateHelper(url, html).GetImages(new List<ReplaceInfo>(), new List<ThumbnailInfo>());
            List<ImageInfo> imagesWithoutReplaces = CreateHelper(url, html).GetImages(null, new List<ThumbnailInfo>());
            CollectionAssert.AreEqual(images.Select(i => i.URL).ToArray(), imagesWithoutReplaces.Select(i => i.URL).ToArray());
            return images;
        }

        private static SiteHelper CreateHelper(string url, string html) {
            SiteHelper helper = SiteHelpers.GetInstance(new Uri(url.Replace("[", "")).Host);
            helper.SetURL(url);
            helper.SetHTMLParser(new HTMLParser("<html><body>" + html + "</body></html>"));
            return helper;
        }

        private static string FourChanPost(string id, string imageURL) {
            return "<div class=\"post reply\" id=\"" + id + "\"><div class=\"file\"><div class=\"fileText\">File: <a href=\"" + imageURL + "\">a.jpg</a></div>" +
                "<a class=\"fileThumb\" href=\"" + imageURL + "\"><img src=\"//i.4cdn.org/wg/" + id + "s.jpg\"></a></div></div>";
        }

        private static string FourChanThread(string containers) {
            return "<div class=\"thread\" id=\"t1\">" + containers + "</div>";
        }

        private static string FourChanContainer(string id, string message) {
            string idAttribute = id != null ? " id=\"" + id + "\"" : String.Empty;
            return "<div class=\"postContainer replyContainer\"" + idAttribute + "><div class=\"post reply\"><input type=\"checkbox\">" +
                "<blockquote class=\"postMessage\">" + message + "</blockquote></div></div>";
        }

        private static string InfinitechanThread(string posts) {
            return "<div class=\"thread\" id=\"thread_100\">" + posts + "</div>";
        }

        private static string InfinitechanPost(string id, string body) {
            string idAttribute = id != null ? " id=\"" + id + "\"" : String.Empty;
            return "<div class=\"post reply\"" + idAttribute + "><p class=\"intro\"><input type=\"checkbox\"><span class=\"name\">Anonymous</span></p>" +
                "<div class=\"body\">" + body + "</div></div>";
        }

        private static string InfinitechanReply(string id, string files, bool withName) {
            string name = withName ? "<span class=\"name\">Carol</span>" : String.Empty;
            return "<div class=\"post reply has-file\" id=\"" + id + "\"><p class=\"intro\"><label>" + name + "</label></p>" + files + "<div class=\"body\">x</div></div>";
        }

        private static string InfinitechanFiles(params string[] imageURLs) {
            return "<div class=\"files\">" + String.Concat(imageURLs.Select(imageURL =>
                "<div class=\"file\"><p class=\"fileinfo\">File: <a href=\"" + imageURL + "\">a.jpg</a> <span class=\"postfilename\">a.jpg</span></p>" +
                "<a href=\"" + imageURL + "\" target=\"_blank\"><img src=\"/file_store/thumb/a.jpg\" data-md5=\"AQIDBAUGBwgJCgsMDQ4PEA==\"></a></div>")) + "</div>";
        }

        private static string FuukaPost(string id, string imageURL, string fileName) {
            return "<div id=\"" + id + "\"><span>File: 1 KB, 1x1, " + fileName + "</span>" +
                "<label><input type=\"checkbox\"><span class=\"postername\">Anonymous</span></label>" +
                "<a href=\"" + imageURL + "\"><img src=\"https://i.warosu.org/data/g/thumb/" + id + "s.jpg\" class=\"thumb\"></a></div>";
        }

        private static string FuukaPostWithSameImageLink(string id, string imageURL, string linkHref) {
            return "<div id=\"" + id + "\"><span>File: 1 KB, 1x1, a.jpg</span><a href=\"" + linkHref + "\">View same</a>" +
                "<label><input type=\"checkbox\"><span class=\"postername\">Anonymous</span></label>" +
                "<a href=\"" + imageURL + "\"><img src=\"https://i.warosu.org/data/g/thumb/" + id + "s.jpg\" class=\"thumb\"></a></div>";
        }

        private static string FoolFuukaPost(string id, string imageURL) {
            return "<article class=\"post has_image\" id=\"" + id + "\"><header><span class=\"post_poster_data\"><span class=\"post_author\">Frank</span></span></header>" +
                "<div class=\"post_file\"><a href=\"" + imageURL + "\" class=\"post_file_filename\">a.jpg</a></div>" +
                "<div class=\"thread_image_box\"><a href=\"" + imageURL + "\" class=\"thread_image_link\"><img src=\"https://i.4pcdn.org/tg/" + id + "s.jpg\" data-md5=\"AQIDBAUGBwgJCgsMDQ4PEA==\"></a></div></article>";
        }

        private static string LynxChanUpload(string imageURL) {
            return "<figure class=\"uploadCell\"><a class=\"nameLink\" href=\"" + imageURL + "\">Open</a><a class=\"originalNameLink\">a.png</a>" +
                "<a class=\"imgLink\" href=\"" + imageURL + "\"><img src=\"/.media/t_" + imageURL.Length + "\"></a></figure>";
        }
    }
}
