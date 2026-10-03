using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Pins current parser behavior for edge cases before refactoring ParseTags.
    [TestClass]
    public class HTMLParserCharacterizationTests {
        private static string Describe(string html) {
            var parser = new HTMLParser(html);
            return string.Join(" ", parser.Tags.Select(t =>
                (t.IsEnd ? "/" : "") + t.Name + (t.IsSelfClosing ? "/" : "") + "@" + t.Offset + "+" + t.Length +
                "[" + string.Join(",", t.Attributes.Select(a => a.Name + "=" + a.Value + "@" + a.Offset + "+" + a.Length)) + "]"));
        }

        [TestMethod]
        [DataRow("<?xml version=\"1.0\"?><p>", "p@21+3[]")]
        [DataRow("<!--><p>", "p@5+3[]")]
        [DataRow("<!---><p>", "p@6+3[]")]
        [DataRow("<!----><p>", "p@7+3[]")]
        [DataRow("<!-- a -- ><p>--><i>", "i@17+3[]")]
        [DataRow("<!-- a --!><p>", "p@11+3[]")]
        [DataRow("<!-- a -!><p>--><i>", "i@16+3[]")]
        [DataRow("<!-- never closed <p>", "")]
        [DataRow("<!DOCTYPE html><p>", "p@15+3[]")]
        [DataRow("<!DOCTYPE html", "")]
        [DataRow("< p></ p><p>", "p@9+3[]")]
        public void SkipsCommentsAndBogusMarkup(string html, string expected) {
            Assert.AreEqual(expected, Describe(html));
        }

        [TestMethod]
        [DataRow("<textarea><b>x</b></textarea><i>", "textarea@0+10[] /textarea@18+11[] i@29+3[]")]
        [DataRow("<title>a<b</TITLE ><p>", "title@0+7[] /title@10+9[] p@19+3[]")]
        [DataRow("<style>p{}</style/><p>", "style@0+7[] /style/@10+9[] p@19+3[]")]
        [DataRow("<script>x</scriptx></script>", "script@0+8[] /script@19+9[]")]
        [DataRow("<script>no end <p>", "script@0+8[]")]
        [DataRow("<script/><p>", "script/@0+9[] p@9+3[]")]
        public void TreatsRawTextElementContentsAsText(string html, string expected) {
            Assert.AreEqual(expected, Describe(html));
        }

        [TestMethod]
        [DataRow("<input disabled checked=checked>", "input@0+32[disabled=@7+9,checked=checked@16+15]")]
        [DataRow("<a href = \"x\" >", "a@0+15[href=x@3+10]")]
        [DataRow("<a href=x/>", "a@0+11[href=x/@3+7]")]
        [DataRow("<a/b>", "a@0+5[b=@3+1]")]
        [DataRow("<br />", "br/@0+6[]")]
        [DataRow("<a\nhref='1'\tid=\"2\">", "a@0+19[href=1@3+8,id=2@12+6]")]
        [DataRow("<a b='1>", "")]
        [DataRow("<a b=1", "")]
        [DataRow("<a b", "")]
        [DataRow("<a", "")]
        [DataRow("</A>", "/a@0+4[]")]
        [DataRow("<a =x>", "a@0+6[=x=@3+2]")]
        public void ParsesAttributeEdgeCases(string html, string expected) {
            Assert.AreEqual(expected, Describe(html));
        }

        [TestMethod]
        public void FindCorrespondingEndTagHonorsStopTagAndSelfClosing() {
            var parser = new HTMLParser("<div><br/><div></div></div><p>");
            HTMLTag outer = parser.Tags[0];
            HTMLTag br = parser.Tags[1];

            Assert.AreSame(parser.Tags[4], parser.FindCorrespondingEndTag(outer));
            Assert.IsNull(parser.FindCorrespondingEndTag(outer, parser.Tags[4]));
            Assert.AreSame(br, parser.FindCorrespondingEndTag(br));
            Assert.IsNull(parser.FindCorrespondingEndTag(null));
            Assert.ThrowsExactly<System.ArgumentException>(() => parser.FindCorrespondingEndTag(parser.Tags[3]));
        }
    }
}
