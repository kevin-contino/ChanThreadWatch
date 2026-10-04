using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Pins current behavior of previously untested General helpers before refactoring.
    [TestClass]
    public class GeneralCharacterizationTests {
        private const string Page = "http://a.com/b/c";

        private static string RefreshPage(string content) {
            return "<html><head><meta http-equiv=\"Refresh\" content=\"" + content + "\"></head></html>";
        }

        // MP-2c: names are shortened so the full path fits the 259 characters the .NET Framework app could
        // create (it was not long path aware). .NET 10 creates longer paths, which would rename files.
        // Recorded on .NET Framework 4.8: dir 40 -> 218, dir 161 -> 97 (259 in total each).
        [TestMethod]
        [DataRow(1)]
        [DataRow(120)]
        public void MaximumFileNameLengthKeepsTheNetFrameworkPathLimit(int padding) {
            string dir = Path.Combine(Path.GetTempPath(), "ctw-len-" + Guid.NewGuid().ToString("N") + new string('x', padding));
            Directory.CreateDirectory(dir);
            try {
                Assert.AreEqual(General.MaxFilePathLength - (dir.Length + 1), General.GetMaximumFileNameLength(dir));
            }
            finally {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        [DataRow("0; url=http://example.com/next", "http://example.com/next")]
        [DataRow("5;URL='../up'", "http://a.com/up")]
        [DataRow("1.5; url=/f", "http://a.com/f")]
        [DataRow("  0 ;  URL = 'x' ", "http://a.com/b/x'")]
        [DataRow("0;url= q ", "http://a.com/b/q")]
        [DataRow("0 url=/z", null)]
        [DataRow("0; uri=/z", null)]
        [DataRow("0; url /z", null)]
        [DataRow("5", null)]
        [DataRow("0; url=", null)]
        public void GetRedirectUrlParsesRefreshContent(string content, string expected) {
            Assert.AreEqual(expected, General.GetRedirectUrl(RefreshPage(content), Page));
        }

        [TestMethod]
        public void GetRedirectUrlUsesFirstNonEmptyRefreshMetaInHead() {
            Assert.AreEqual("http://a.com/z", General.GetRedirectUrl(
                "<head><meta http-equiv=\"content-type\" content=\"x\"><meta http-equiv=\"REFRESH\" content=\"1; url=/z\"></head>", Page));
            Assert.AreEqual("http://a.com/ok", General.GetRedirectUrl(
                "<head><meta http-equiv=refresh content=\"\"><meta http-equiv=refresh content=\"0;url=/ok\"></head>", Page));
            Assert.IsNull(General.GetRedirectUrl(
                "<head><meta http-equiv=refresh content=\"0 x\"><meta http-equiv=refresh content=\"0;url=/ok\"></head>", Page));
        }

        [TestMethod]
        public void GetRedirectUrlReturnsNullWithoutHead() {
            Assert.IsNull(General.GetRedirectUrl("<meta http-equiv=\"Refresh\" content=\"0; url=/z\">", Page));
            Assert.IsNull(General.GetRedirectUrl("<head><meta http-equiv=\"Refresh\" content=\"0; url=/z\">", Page));
            Assert.IsNull(General.GetRedirectUrl(RefreshPage("0; url=/z"), "not a url"));
        }

        [TestMethod]
        [DataRow(null, null)]
        [DataRow("text/html", null)]
        [DataRow("text/html; charset=UTF-8", "UTF-8")]
        [DataRow(" text/html ;CHARSET = utf-8 ", "utf-8")]
        [DataRow("text/html; charset=\"shift_jis\"", "shift_jis")]
        [DataRow("text/html; charset='euc-jp", "euc-jp")]
        [DataRow("text/html; charset=\" a b \"", "a b")]
        [DataRow("text/html; charset=\"a\"b", "a")]
        [DataRow("text/html; charset=\"\"", null)]
        [DataRow("text/html; charset=\"", null)]
        [DataRow("text/html; charset=", null)]
        [DataRow("text/html; foo=bar; charset=x; charset=y", "x")]
        [DataRow("charset=;charset=x", null)]
        public void GetCharSetFromContentTypeReadsCharsetParameter(string contentType, string expected) {
            Assert.AreEqual(expected, General.GetCharSetFromContentType(contentType));
        }

        [TestMethod]
        public void DetectHTMLEncodingIgnoresTruncatedBOMs() {
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(new byte[0], "text/html").WebName, true);
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(new byte[] { 0xEF }, "text/html").WebName, true);
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(new byte[] { 0xEF, 0xBB }, "text/html").WebName, true);
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(new byte[] { 0xFE }, "text/html").WebName, true);
        }

        [TestMethod]
        public void DetectHTMLEncodingKeepsBOMPreambleForUTF16() {
            Encoding le = General.DetectHTMLEncoding(new byte[] { 0xFF, 0xFE, (byte)'<', 0 }, "text/html; charset=utf-16");
            Encoding noBom = General.DetectHTMLEncoding(new byte[] { (byte)'<', 0 }, "text/html; charset=utf-16");

            Assert.HasCount(2, le.GetPreamble());
            Assert.IsEmpty(noBom.GetPreamble());
        }

        [TestMethod]
        public void DetectHTMLEncodingReadsXMLDeclarationForXMLTypes() {
            byte[] declared = Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"iso-8859-2\"?><html>");
            byte[] emptyEncoding = Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"\"?><html>");
            byte[] noDeclaration = Encoding.ASCII.GetBytes("<html><meta charset=\"shift_jis\">");

            Assert.AreEqual("iso-8859-2", General.DetectHTMLEncoding(declared, "application/xhtml+xml").WebName);
            Assert.AreEqual("utf-8", General.DetectHTMLEncoding(emptyEncoding, "text/xml").WebName);
            Assert.AreEqual("utf-8", General.DetectHTMLEncoding(noDeclaration, "application/xml").WebName);
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(declared, "text/html").WebName, true);
        }

        [TestMethod]
        public void DetectHTMLEncodingFallsBackToHttpEquivWhenCharsetAttributeIsEmpty() {
            byte[] bytes = Encoding.ASCII.GetBytes("<meta charset=\"\" http-equiv=\" Content-Type \" content=\"text/html; charset=euc-jp\">");

            Assert.AreEqual("euc-jp", General.DetectHTMLEncoding(bytes, "text/html").WebName);
        }

        [TestMethod]
        [DataRow("  boards.4chan.org/g/thread/1#p2  ", "http://boards.4chan.org/g/thread/1")]
        [DataRow("HTTPS://Boards.4chan.org/g/", "https://boards.4chan.org/g/")]
        [DataRow("http://a.com/x y", "http://a.com/x%20y")]
        [DataRow("", null)]
        [DataRow("   ", null)]
        [DataRow("#frag", null)]
        [DataRow("boards.4chan.org", null)]
        [DataRow("http://", null)]
        [DataRow("http://a b/c", null)]
        public void CleanPageURLNormalizesInput(string url, string expected) {
            Assert.AreEqual(expected, General.CleanPageURL(url));
        }

        [TestMethod]
        public void IsFileNameTooLongProbesWithTemporaryFile() {
            string dir = Path.Combine(Path.GetTempPath(), "ctw_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                Assert.IsFalse(General.IsFileNameTooLong(dir, 10));
                Assert.IsEmpty(Directory.GetFiles(dir));

                for (char c = 'a'; c <= 'z'; c++) {
                    File.WriteAllText(Path.Combine(dir, new string(c, 3)), "");
                }
                Exception ex = Assert.ThrowsExactly<Exception>(() => General.IsFileNameTooLong(dir, 3));
                Assert.AreEqual("Unable to determine if filename is too long.", ex.Message);
                Assert.IsFalse(General.IsFileNameTooLong(dir, 4));
            }
            finally {
                Directory.Delete(dir, true);
            }
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => General.IsFileNameTooLong(dir, 10));
        }

        private static List<ReplaceInfo> OtherReplaces(string html, string pageURL, params int[] existingOffsets) {
            var replaces = existingOffsets.Select(o => new ReplaceInfo { Offset = o, Length = 1, Value = "existing" }).ToList();
            General.AddOtherReplaces(new HTMLParser(html), pageURL, replaces);
            return replaces.Skip(existingOffsets.Length).Where(r => r.Value != General.ActiveContentPolicyMeta).ToList();
        }

        // A page without a head or html start tag gets the policy inserted before its first content
        [TestMethod]
        public void AddOtherReplacesInsertsThePolicyIntoAPageWithoutAHead() {
            var replaces = new List<ReplaceInfo>();
            General.AddOtherReplaces(new HTMLParser("<!DOCTYPE html><p>a</p>"), Page, replaces);

            ReplaceInfo policy = replaces.Single(r => r.Value == General.ActiveContentPolicyMeta);
            Assert.AreEqual(15, policy.Offset);
            Assert.AreEqual(0, policy.Length);
        }

        [TestMethod]
        public void AddOtherReplacesRewritesLinksAndRemovesBaseAndScripts() {
            const string html = "<base href=\"x\"><a href=\"#top\">t</a><img src=\"i.jpg\"><script src=s.js></script><link href=\"c.css\"><a name=n><a href=\"?a=1&amp;b=2\">";

            List<ReplaceInfo> replaces = OtherReplaces(html, Page);

            CollectionAssert.AreEqual(
                new[] { "", "", "href=\"#top\"", "src=\"http://a.com/b/i.jpg\"", "href=\"http://a.com/b/c.css\"", "href=\"http://a.com/b/c?a=1&amp;b=2\"" },
                replaces.Select(r => r.Value).ToArray());
            CollectionAssert.AreEqual(
                new[] { "<base href=\"x\">", "<script src=s.js></script>", "href=\"#top\"", "src=\"i.jpg\"", "href=\"c.css\"", "href=\"?a=1&amp;b=2\"" },
                replaces.Select(r => html.Substring(r.Offset, r.Length)).ToArray());
            Assert.IsTrue(replaces.All(r => r.Type == ReplaceType.Other));
        }

        [TestMethod]
        public void AddOtherReplacesSkipsExistingOffsetsAndUnresolvableURLs() {
            const string html = "<img src=\"i.jpg\"><a href=\"y\">";

            List<ReplaceInfo> replaces = OtherReplaces(html, Page, 5);

            CollectionAssert.AreEqual(new[] { "href=\"http://a.com/b/y\"" }, replaces.Select(r => r.Value).ToArray());
            Assert.IsEmpty(OtherReplaces(html, "not a url"));
        }

        [TestMethod]
        public void AddOtherReplacesConvertsNewLines() {
            List<ReplaceInfo> replaces = OtherReplaces("a\nb\r\nc", Page);

            if (Environment.NewLine == "\n") {
                Assert.IsEmpty(replaces);
                return;
            }
            CollectionAssert.AreEqual(new[] { 1, 3 }, replaces.Select(r => r.Offset).ToArray());
            Assert.IsTrue(replaces.All(r => r.Length == 1 && r.Value == Environment.NewLine));
        }
    }
}
