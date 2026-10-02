using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    [TestClass]
    public class HTMLParserTests {
        [TestMethod]
        public void ParsesStartAndEndTagsWithAttributes() {
            var parser = new HTMLParser("<a href=\"x.jpg\" class='b c'>text</a>");

            Assert.HasCount(2, parser.Tags);
            HTMLTag start = parser.Tags[0];
            Assert.AreEqual("a", start.Name);
            Assert.IsFalse(start.IsEnd);
            Assert.AreEqual("x.jpg", start.GetAttributeValue("href"));
            Assert.AreEqual("b c", start.GetAttributeValue("class"));
            Assert.IsTrue(parser.Tags[1].IsEnd);
        }

        [TestMethod]
        public void LowercasesNamesAndReadsUnquotedValues() {
            var parser = new HTMLParser("<DIV ID=Main Data-X=1>");

            HTMLTag tag = parser.Tags.Single();
            Assert.AreEqual("div", tag.Name);
            Assert.AreEqual("Main", tag.GetAttributeValue("id"));
            Assert.AreEqual("1", tag.GetAttributeValue("data-x"));
        }

        [TestMethod]
        public void KeepsFirstOfDuplicateAttributes() {
            var parser = new HTMLParser("<img src=\"first\" src=\"second\">");

            HTMLTag tag = parser.Tags.Single();
            Assert.HasCount(1, tag.Attributes);
            Assert.AreEqual("first", tag.GetAttributeValue("src"));
        }

        [TestMethod]
        public void RecordsAttributeOffsetAndLength() {
            const string html = "<a href=\"x\">";
            var parser = new HTMLParser(html);

            HTMLAttribute attribute = parser.Tags.Single().GetAttribute("href");
            Assert.AreEqual("href=\"x\"", html.Substring(attribute.Offset, attribute.Length));
        }

        [TestMethod]
        public void DetectsSelfClosingTags() {
            var parser = new HTMLParser("<br/><p>");

            Assert.IsTrue(parser.Tags[0].IsSelfClosing);
            Assert.IsFalse(parser.Tags[1].IsSelfClosing);
        }

        [TestMethod]
        public void StopsAtUnterminatedTagWithoutThrowing() {
            var parser = new HTMLParser("<p>ok</p><a href=\"never-closed");

            CollectionAssert.AreEqual(new[] { "p", "p" }, parser.Tags.Select(t => t.Name).ToArray());
        }

        [TestMethod]
        public void TreatsScriptContentsAsRawText() {
            var parser = new HTMLParser("<script>if (a<b) { x = '<div>'; }</script><p>");

            CollectionAssert.AreEqual(new[] { "script", "script", "p" }, parser.Tags.Select(t => t.Name).ToArray());
        }

        [TestMethod]
        public void SkipsCommentsAndDoctype() {
            var parser = new HTMLParser("<!DOCTYPE html><!-- <a href=\"hidden\"> --><p>");

            Assert.AreEqual("p", parser.Tags.Single().Name);
        }

        [TestMethod]
        public void NormalizesLineEndings() {
            var parser = new HTMLParser("a\r\nb\rc\nd");

            Assert.AreEqual("a\nb\nc\nd", parser.PreprocessedHTML);
        }

        [TestMethod]
        public void TagRangeMatchesNestedEndTag() {
            var parser = new HTMLParser("<div id=\"outer\"><div>in</div>out</div>");

            HTMLTagRange range = parser.CreateTagRange(parser.FindTagById("outer"));
            Assert.AreEqual("<div>in</div>out", parser.GetInnerHTML(range));
        }

        [TestMethod]
        public void TagRangeIsNullWhenEndTagIsMissing() {
            var parser = new HTMLParser("<div id=\"outer\"><span>text");

            Assert.IsNull(parser.CreateTagRange(parser.FindTagById("outer")));
        }

        [TestMethod]
        public void ClassAttributeMatchesWholeClassNamesOnly() {
            Assert.IsTrue(HTMLParser.ClassAttributeValueHas("post reply", "post"));
            Assert.IsFalse(HTMLParser.ClassAttributeValueHas("postContainer", "post"));
            Assert.IsFalse(HTMLParser.ClassAttributeValueHas("Post", "post"));
        }
    }
}
