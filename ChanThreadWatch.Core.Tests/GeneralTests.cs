using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class GeneralTests {
        [TestMethod]
        [DataRow("http://user:secret@boards.example.test/a/thread/1", "http://boards.example.test/a/thread/1")]
        [DataRow("https://user@boards.example.test:8443/a/thread/1#p2", "https://boards.example.test:8443/a/thread/1#p2")]
        [DataRow("http://user:p%40ss@127.0.0.1:8080/a/res/1.html?x=1", "http://127.0.0.1:8080/a/res/1.html?x=1")]
        [DataRow("http://boards.example.test/a/thread/1", "http://boards.example.test/a/thread/1")]
        [DataRow("not a url", "not a url")]
        [DataRow("not a url@host", null)]
        [DataRow(null, null)]
        public void RemoveUserInfoDropsOnlyTheLogin(string url, string expected) {
            Assert.AreEqual(expected, General.RemoveUserInfo(url));
        }

        [TestMethod]
        public void ParseVersionNumberPacksComponents() {
            Assert.AreEqual((1 << 24) | (17 << 16) | (2 << 8), General.ParseVersionNumber("1.17.2"));
            Assert.AreEqual((1 << 24) | (17 << 16) | (2 << 8) | 3, General.ParseVersionNumber("1.17.2.3"));
            Assert.AreEqual(1 << 24, General.ParseVersionNumber("1"));
            Assert.AreEqual((1 << 24) | (17 << 16), General.ParseVersionNumber("1.17"));
        }

        [TestMethod]
        public void ParseVersionNumberMasksOversizedComponents() {
            Assert.AreEqual((1 << 24) | ((300 & 0xFF) << 16), General.ParseVersionNumber("1.300"));
            Assert.AreEqual((200 & 0x7F) << 24, General.ParseVersionNumber("200"));
        }

        [TestMethod]
        public void ParseVersionNumberOrdersReleases() {
            Assert.IsGreaterThan(General.ParseVersionNumber("1.17.1"), General.ParseVersionNumber("1.17.2"));
            Assert.IsGreaterThan(General.ParseVersionNumber("1.17.9"), General.ParseVersionNumber("1.18.0"));
            Assert.IsGreaterThan(General.ParseVersionNumber("1.255.255"), General.ParseVersionNumber("2.0.0"));
        }

        [TestMethod]
        [DataRow("{\"url\":\"x\",\"tag_name\":\"v1.18.0\",\"name\":\"v1.18.0\"}", "v1.18.0")]
        [DataRow("{\"tag_name\" : \"1.17.2\"}", "1.17.2")]
        [DataRow("{\"message\":\"Not Found\"}", null)]
        [DataRow("", null)]
        [DataRow(null, null)]
        public void ParseReleaseTagNameReadsTagFromReleasesApi(string json, string expected) {
            Assert.AreEqual(expected, General.ParseReleaseTagName(json));
        }

        // B5 / S7
        [TestMethod]
        [DataRow("v1.17.2", "1.17.2")]
        [DataRow("V1.18.0", "1.18.0")]
        [DataRow("1.17.3", "1.17.3")]
        [DataRow("  v2.0.0\r\n", "2.0.0")]
        [DataRow("v1.0", "1.0")]
        [DataRow("v1.17.2.4", "1.17.2.4")]
        [DataRow("v01.017.2", "1.17.2")]
        public void NormalizeUpdateVersionAcceptsPlausibleTags(string tag, string expected) {
            Assert.AreEqual(expected, General.NormalizeUpdateVersion(tag, "1.17.1"));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("v")]
        [DataRow("vv1.17.2")]
        [DataRow("1.v17.2")]
        [DataRow("1.17.2v")]
        [DataRow("1.17.2-beta")]
        [DataRow("1.17.-2")]
        [DataRow("1..2")]
        [DataRow("1.17.2.3.4")]
        [DataRow("1.256.0")]
        [DataRow("1.17.300")]
        [DataRow("128.0.0")]
        [DataRow("127.255.255")]
        [DataRow("3.0.0")]
        [DataRow("99999999999.0.0")]
        [DataRow("<b>1.18.0</b>")]
        public void NormalizeUpdateVersionRejectsImplausibleTags(string tag) {
            Assert.IsNull(General.NormalizeUpdateVersion(tag, "1.17.1"));
        }

        [TestMethod]
        public void NormalizeUpdateVersionRejectsAllTagsForUnparseableCurrentVersion() {
            Assert.IsNull(General.NormalizeUpdateVersion("1.17.2", "garbage"));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("abc")]
        [DataRow("v1.17.2")]
        [DataRow("1.x.2")]
        public void ParseVersionNumberRejectsInvalidInput(string version) {
            Assert.AreEqual(-1, General.ParseVersionNumber(version));
        }

        [TestMethod]
        // PendingUnix: Path.GetInvalidFileNameChars on Unix holds only '/' and NUL, so the Windows-invalid characters stay in the name, see MP-4b
        [TestCategory("PendingUnix")]
        public void CleanFileNameRemovesInvalidCharacters() {
            Assert.AreEqual("abcdefghij", General.CleanFileName("a<b>c:d\"e/f\\g|h?i*j"));
            Assert.AreEqual("tab", General.CleanFileName("t\ta\0b"));
        }

        [TestMethod]
        public void CleanFileNameKeepsValidNames() {
            Assert.AreEqual("mountain & lake (1).jpg", General.CleanFileName("mountain & lake (1).jpg"));
        }

        [TestMethod]
        public void GetAbsoluteURLResolvesRelativeForms() {
            const string page = "https://boards.4chan.org/wg/thread/8143532";

            Assert.AreEqual("https://i.4cdn.org/wg/1.jpg", General.GetAbsoluteURL(page, "//i.4cdn.org/wg/1.jpg"));
            Assert.AreEqual("https://boards.4chan.org/wg/catalog", General.GetAbsoluteURL(page, "../catalog"));
            Assert.AreEqual("https://boards.4chan.org/wg/thread/8143532#p1", General.GetAbsoluteURL(page, "#p1"));
            Assert.AreEqual("http://example.com/", General.GetAbsoluteURL(page, "http://example.com/"));
        }

        // Runs on every OS. A root-relative link ("/b/src/1.jpg") must resolve against the page, never
        // to an implicit Unix file path (file:///b/src/1.jpg): Uri.TryCreate(Uri, string) parses the
        // link as RelativeOrAbsolute, where a relative Uri wins over an implicit Unix path.
        [TestMethod]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "/b/src/1.jpg", "https://boards.4chan.org/b/src/1.jpg")]
        [DataRow("http://example.com/b/res/1.html", "/b/src/1001.jpg", "http://example.com/b/src/1001.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "/", "https://boards.4chan.org/")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "/b/../c/1.jpg", "https://boards.4chan.org/c/1.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "/b/src/a b.jpg", "https://boards.4chan.org/b/src/a%20b.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "/b/src/1.jpg?x=1#f", "https://boards.4chan.org/b/src/1.jpg?x=1#f")]
        [DataRow("http://example.com/b/res/1.html", "/redirect/src/http://other.example.org/img/1.jpg", "http://example.com/redirect/src/http://other.example.org/img/1.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "\\b\\src\\1.jpg", "https://boards.4chan.org/b/src/1.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "//i.4cdn.org/wg/1.jpg", "https://i.4cdn.org/wg/1.jpg")]
        [DataRow("http://example.com/b/res/1.html", "//cdn.example.com/b/thumb/1s.jpg", "http://cdn.example.com/b/thumb/1s.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "../catalog", "https://boards.4chan.org/wg/catalog")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "1.jpg", "https://boards.4chan.org/wg/thread/1.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "?page=2", "https://boards.4chan.org/wg/thread/8143532?page=2")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "#p1", "https://boards.4chan.org/wg/thread/8143532#p1")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "", "https://boards.4chan.org/wg/thread/8143532")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "http://example.com/", "http://example.com/")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "HTTPS://Example.COM/A", "https://example.com/A")]
        [DataRow("http://example.com/b/res/1.html", "ftp://files.example.net/src/1.jpg", "ftp://files.example.net/src/1.jpg")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "mailto:someone@example.com", "mailto:someone@example.com")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "javascript:void(0)", "javascript:void(0)")]
        [DataRow("https://boards.4chan.org/wg/thread/8143532", "data:image/png;base64,AAAA", "data:image/png;base64,AAAA")]
        public void GetAbsoluteURLResolvesTheSameOnEveryOS(string baseURL, string relativeURL, string expected) {
            Assert.AreEqual(expected, General.GetAbsoluteURL(baseURL, relativeURL));
        }

        [TestMethod]
        public void GetAbsoluteURLReturnsNullForInvalidBase() {
            Assert.IsNull(General.GetAbsoluteURL("not a url", "x.jpg"));
        }

        [TestMethod]
        public void DetectHTMLEncodingPrefersContentTypeHeader() {
            byte[] bytes = Encoding.ASCII.GetBytes("<meta charset=\"shift_jis\">");

            Assert.AreEqual("utf-8", General.DetectHTMLEncoding(bytes, "text/html; charset=utf-8").WebName);
            Assert.AreEqual("iso-8859-1", General.DetectHTMLEncoding(bytes, "text/html; charset=\"ISO-8859-1\"").WebName);
        }

        [TestMethod]
        public void DetectHTMLEncodingUsesBOM() {
            Encoding utf8 = General.DetectHTMLEncoding(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<' }, "text/html");
            Assert.AreEqual("utf-8", utf8.WebName);
            Assert.HasCount(3, utf8.GetPreamble());

            Assert.AreEqual("utf-16", General.DetectHTMLEncoding(new byte[] { 0xFF, 0xFE, (byte)'<', 0 }, "text/html").WebName);
            Assert.AreEqual("utf-16BE", General.DetectHTMLEncoding(new byte[] { 0xFE, 0xFF, 0, (byte)'<' }, "text/html").WebName);
        }

        [TestMethod]
        public void DetectHTMLEncodingReadsMetaTags() {
            byte[] metaCharset = Encoding.ASCII.GetBytes("<html><head><meta charset=\"shift_jis\"></head>");
            byte[] httpEquiv = Encoding.ASCII.GetBytes("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=euc-jp\">");

            Assert.AreEqual("shift_jis", General.DetectHTMLEncoding(metaCharset, "text/html").WebName);
            Assert.AreEqual("euc-jp", General.DetectHTMLEncoding(httpEquiv, "text/html").WebName);
        }

        [TestMethod]
        public void DetectHTMLEncodingFallsBackToWindows1252() {
            byte[] bytes = Encoding.ASCII.GetBytes("<html><body>plain</body></html>");

            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(bytes, null).WebName, true);
            Assert.AreEqual("Windows-1252", General.DetectHTMLEncoding(bytes, "text/html; charset=not-a-real-charset").WebName, true);
        }

        // MP-4a: the code pages come from CodePagesEncodingProvider (registered in General's static
        // constructor), not from the OS, so a page in each decodes to the same text on every OS
        [TestMethod]
        [DataRow(null, new byte[] { 0x80, 0x20, 0x93, 0x71, 0x94, 0x20, 0x63, 0x61, 0x66, 0xE9, 0x20, 0x9F }, "€ “q” café Ÿ")]
        [DataRow("text/html; charset=windows-1252", new byte[] { 0x80, 0x20, 0x93, 0x71, 0x94, 0x20, 0x63, 0x61, 0x66, 0xE9, 0x20, 0x9F }, "€ “q” café Ÿ")]
        [DataRow("text/html; charset=shift_jis", new byte[] { 0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA, 0x20, 0xB1, 0x20, 0x82, 0xA0 }, "日本語 ｱ あ")]
        [DataRow("text/html; charset=euc-jp", new byte[] { 0xC6, 0xFC, 0xCB, 0xDC, 0xB8, 0xEC, 0x20, 0xA4, 0xA2 }, "日本語 あ")]
        public void DetectHTMLEncodingDecodesCodePageText(string contentType, byte[] body, string expected) {
            Assert.AreEqual(expected, General.DetectHTMLEncoding(body, contentType).GetString(body));
        }

        [TestMethod]
        public void DetectHTMLEncodingDecodesShiftJISDeclaredInAMetaTag() {
            byte[] head = Encoding.ASCII.GetBytes("<meta charset=\"shift_jis\"><title>");
            byte[] title = { 0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA };
            byte[] page = new byte[head.Length + title.Length];
            head.CopyTo(page, 0);
            title.CopyTo(page, head.Length);

            Assert.AreEqual("<meta charset=\"shift_jis\"><title>日本語", General.DetectHTMLEncoding(page, "text/html").GetString(page));
        }

        [TestMethod]
        public void TryBase64DecodeReturnsBytesOrNull() {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, General.TryBase64Decode("AQID"));
            Assert.IsNull(General.TryBase64Decode("!!not base64!!"));
        }

        [TestMethod]
        public void HtmlAttributeEncodeOptionallyEscapesHash() {
            Assert.AreEqual("a&amp;&quot;#1", General.HtmlAttributeEncode("a&\"#1"));
            Assert.AreEqual("a&amp;&quot;%231", General.HtmlAttributeEncode("a&\"#1", false));
        }

        [TestMethod]
        public void WriteReplacedStringAppliesReplacementsInOffsetOrder() {
            var replaces = new List<ReplaceInfo> {
                new ReplaceInfo { Offset = 6, Length = 5, Value = "there" },
                new ReplaceInfo { Offset = 0, Length = 5, Value = "HELLO" }
            };

            Assert.AreEqual("HELLO there!", WriteReplaced("hello world!", replaces));
        }

        [TestMethod]
        public void WriteReplacedStringSkipsOverlapsAndStopsAtOutOfRange() {
            var overlapping = new List<ReplaceInfo> {
                new ReplaceInfo { Offset = 0, Length = 5, Value = "X" },
                new ReplaceInfo { Offset = 3, Length = 4, Value = "Y" }
            };
            var outOfRange = new List<ReplaceInfo> {
                new ReplaceInfo { Offset = 8, Length = 10, Value = "Z" }
            };
            var deletion = new List<ReplaceInfo> {
                new ReplaceInfo { Offset = 5, Length = 6, Value = null }
            };

            Assert.AreEqual("X world", WriteReplaced("hello world", overlapping));
            Assert.AreEqual("hello world", WriteReplaced("hello world", outOfRange));
            Assert.AreEqual("hello", WriteReplaced("hello world", deletion));
        }

        private static string WriteReplaced(string str, List<ReplaceInfo> replaces) {
            using (var writer = new StringWriter()) {
                General.WriteReplacedString(str, replaces, writer);
                return writer.ToString();
            }
        }
    }
}
