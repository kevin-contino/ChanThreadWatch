using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class GeneralTests {
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
