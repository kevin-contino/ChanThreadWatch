using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // S5: saved pages keep no code that runs when the page is opened from disk
    [TestClass]
    public class ActiveContentTests {
        private const string Page = "http://a.com/b/c";

        private static string Save(string html, List<ReplaceInfo> replaces = null) {
            replaces = replaces ?? new List<ReplaceInfo>();
            var htmlParser = new HTMLParser(html);
            General.AddOtherReplaces(htmlParser, Page, replaces);
            using (var writer = new StringWriter()) {
                General.WriteReplacedString(htmlParser.PreprocessedHTML, replaces, writer);
                return writer.ToString().Replace(Environment.NewLine, "\n");
            }
        }

        // A page without a head or html start tag gets the policy before its first content
        private static string SaveFragment(string html, List<ReplaceInfo> replaces = null) {
            string saved = Save(html, replaces);
            StringAssert.StartsWith(saved, General.ActiveContentPolicyMeta);
            return saved.Substring(General.ActiveContentPolicyMeta.Length);
        }

        [TestMethod]
        public void RemovesScriptsWithTheirContents() {
            string saved = SaveFragment("<p>a</p><script src=\"s.js\"></script><SCRIPT type=\"text/javascript\">\nvar x = \"<b>\";\n</SCRIPT><p>b</p><svg><script>y()</script></svg>");

            Assert.AreEqual("<p>a</p><p>b</p><svg></svg>", saved);
        }

        [TestMethod]
        public void RemovesEmbeddedContent() {
            string saved = SaveFragment("<iframe src=\"https://x.com/\"><p>no frames</p></iframe>|<object data=\"x.swf\"><embed src=\"x.swf\"></object>|<embed src=\"y.swf\">|<applet code=\"A\"></applet>|<frameset><frame src=\"f.html\"></frameset>");

            Assert.AreEqual("||||<frameset></frameset>", saved);
        }

        [TestMethod]
        public void UnclosedScriptLosesOnlyItsStartTag() {
            Assert.AreEqual("<p>a</p>alert(1)", SaveFragment("<p>a</p><script>alert(1)"));
        }

        [TestMethod]
        public void RemovesEventHandlerAttributes() {
            string saved = SaveFragment("<body onload=\"init()\"><img src=\"http://a.com/i.jpg\" onerror=alert(1) ONCLICK='go()'><a href=\"#p1\" onmouseover=\"preview()\">&gt;&gt;1</a></body>");

            Assert.AreEqual("<body ><img src=\"http://a.com/i.jpg\"  ><a href=\"#p1\" >&gt;&gt;1</a></body>", saved);
        }

        // Browsers ignore a repeated attribute, so after the first is removed the second would apply
        [TestMethod]
        public void RemovesRepeatedEventHandlerAttributes() {
            Assert.AreEqual("<img   >", SaveFragment("<img onerror=a() onerror=b() onerror=c()>"));
        }

        [TestMethod]
        public void RemovesScriptURLAttributes() {
            string saved = SaveFragment("<a href=\"javascript:void(0)\">x</a><form action=\"vbscript:y\"></form><a href=\"#ok\" href=\"javascript:z\">y</a>");

            Assert.AreEqual("<a >x</a><form ></form><a href=\"#ok\" >y</a>", saved);
        }

        [TestMethod]
        [DataRow("javascript:alert(1)")]
        [DataRow("  JaVaScRiPt:alert(1)")]
        [DataRow("java\tscript:alert(1)")]
        [DataRow("java\nscript:alert(1)")]
        [DataRow("&#106;avascript:alert(1)")]
        [DataRow("&#x6A;avascript&#x3A;alert(1)")]
        [DataRow("java&Tab;script:alert(1)")]
        [DataRow("java&NewLine;script:alert(1)")]
        [DataRow("javascript&colon;alert(1)")]
        [DataRow("&#106avascript:alert(1)")]
        [DataRow("jav&#97script:alert(1)")]
        [DataRow("java&#x0Ascript:alert(1)")]
        [DataRow("vbscript:msgbox(1)")]
        public void DetectsScriptURLs(string value) {
            Assert.IsTrue(General.IsScriptURL(value));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("https://a.com/javascript:x")]
        [DataRow("#javascript:x")]
        [DataRow("/js/javascript.js")]
        [DataRow("javascript")]
        [DataRow("data:image/png;base64,AAAA")]
        public void KeepsOtherURLs(string value) {
            Assert.IsFalse(General.IsScriptURL(value));
        }

        [TestMethod]
        public void AddsContentSecurityPolicyToHead() {
            string saved = Save("<html><head lang=\"en\" onclick=\"x()\"><title>t</title></head><body></body></html>");

            Assert.AreEqual("<html><head>" + General.ActiveContentPolicyMeta + "<title>t</title></head><body></body></html>", saved);
        }

        [TestMethod]
        public void PageWithImpliedHeadGetsPolicyAfterHtmlTag() {
            Assert.AreEqual("<html>" + General.ActiveContentPolicyMeta + "<p>a</p></html>", Save("<html lang=\"en\" onclick=\"x()\"><p>a</p></html>"));
        }

        // A page without a head start tag where a browser starts its head, or an html start tag
        // where the head is implied, gets the policy before its first content, after what a
        // browser reads before it starts the page, so the policy lands in the head the browser
        // creates and a doctype still counts
        [TestMethod]
        [DataRow("<p>a</p>", "{P}<p>a</p>")]
        [DataRow("", "{P}")]
        [DataRow("<!DOCTYPE html>\n<title>t</title>", "<!DOCTYPE html>\n{P}<title>t</title>")]
        [DataRow("\n<!-- c --><!---><!--><!-- d --!><?xml x?><!x>\ntext<p>", "\n<!-- c --><!---><!--><!-- d --!><?xml x?><!x>\n{P}text<p>")]
        [DataRow("<!-- never closed <p>", "{P}<!-- never closed <p>")]
        [DataRow("&nbsp;<p>", "{P}&nbsp;<p>")]
        [DataRow("<body onload=\"x()\"><p>a</p>", "{P}<body ><p>a</p>")]
        [DataRow("text<html><head></head></html>", "{P}text<html><head></head></html>")]
        [DataRow("<div>x</div><head></head>", "{P}<div>x</div><head></head>")]
        [DataRow("<table><head></head></table>", "{P}<table><head></head></table>")]
        [DataRow("<noscript><head></head></noscript><p>a</p>", "{P}<noscript><head></head></noscript><p>a</p>")]
        [DataRow("<svg><head></head></svg>", "{P}<svg><head></head></svg>")]
        [DataRow("<!---><html><head></head></html>", "<!--->{P}<html><head></head></html>")]
        [DataRow("\u00A0<html><head></head></html>", "{P}\u00A0<html><head></head></html>")]
        [DataRow("\v<html><head></head></html>", "{P}\v<html><head></head></html>")]
        [DataRow("<!-- c -->\uFEFF<html><head></head></html>", "<!-- c -->{P}\uFEFF<html><head></head></html>")]
        public void PageWithoutAHeadGetsThePolicyBeforeItsContent(string html, string expected) {
            Assert.AreEqual(expected.Replace("{P}", General.ActiveContentPolicyMeta), Save(html));
        }

        // A browser ends the svg at the div's end tag and reads the textarea as text. HTMLParser
        // follows no HTML element outside svg and math, so it keeps the svg open; a textarea with
        // markup read where the parser is unsure is removed, and then the handler is found.
        [TestMethod]
        public void SvgThatAnHTMLEndTagMayHaveEndedStaysUncertain() {
            string saved = Save("<head></head><body><div><svg><g></div><textarea><!--</textarea><img src=x onerror=alert(1)>--></body>");

            Assert.AreEqual("<head>" + General.ActiveContentPolicyMeta + "</head><body><div><svg><g></div><img src=\"http://a.com/b/x\" >--></body>", saved);
        }

        // Comments end where a browser ends them, so a script after a comment is found
        [TestMethod]
        [DataRow("<!--><script>alert(1)</script>", "<!-->")]
        [DataRow("<!---><script>alert(1)</script>-->", "<!--->-->")]
        [DataRow("<!----><script>alert(1)</script>", "<!---->")]
        [DataRow("<!-- a --!><script>alert(1)</script>-->", "<!-- a --!>-->")]
        [DataRow("<!-- a --><script>alert(1)</script>", "<!-- a -->")]
        public void RemovesScriptAfterComment(string html, string expected) {
            // Text first, so the policy goes before the comment
            Assert.AreEqual("a" + expected, SaveFragment("a" + html));
        }

        // A browser reads these scripts as part of the comment, so they don't run
        [TestMethod]
        [DataRow("<!-- a -- ><script>alert(1)</script>-->")]
        [DataRow("<!-- a --!-><script>alert(1)</script>-->")]
        [DataRow("<!-- never closed <script>alert(1)</script>")]
        public void KeepsScriptTextInsideComment(string html) {
            Assert.AreEqual("a" + html, SaveFragment("a" + html));
        }

        // Inside svg and math (also in the HTML of their integration points) and inside a select,
        // a browser may read the contents of title, style, textarea and the other raw text
        // elements as markup. The parser reads them as text, and an element whose contents hold
        // markup is removed with them, so the saved page has nothing the two readings disagree
        // on; what follows is then read as a browser reads it.
        [TestMethod]
        [DataRow("<svg><title><script>alert(1)</script></title></svg>", "<svg></svg>")]
        [DataRow("<SVG><TITLE><script>alert(1)</script></TITLE></SVG>", "<SVG></SVG>")]
        [DataRow("<math><title><img onerror=alert(1)></title></math>", "<math></math>")]
        [DataRow("<svg><style><script>alert(1)</script></style></svg>", "<svg></svg>")]
        [DataRow("<svg><font><title><script>alert(1)</script></title></font></svg>", "<svg><font></font></svg>")]
        [DataRow("<svg><g><textarea><img onerror=alert(1)></textarea></g></svg>", "<svg><g></g></svg>")]
        [DataRow("<svg><xmp><img onerror=alert(1)></xmp></svg>", "<svg></svg>")]
        [DataRow("<svg><foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea></foreignObject></svg>", "<svg><foreignObject>--></textarea></foreignObject></svg>")]
        [DataRow("<svg><desc><style><!--</style><script>alert(1)</script>--></style></desc></svg>", "<svg><desc>--></style></desc></svg>")]
        [DataRow("<math><mi><title><!--</title><script>alert(1)</script>--></title></mi></math>", "<math><mi>--></title></mi></math>")]
        [DataRow("<math><mi><title><script>alert(1)</script></title></mi></math>", "<math><mi></mi></math>")]
        [DataRow("<math><mi><mglyph><title><img onerror=alert(1)></title></mglyph></mi></math>", "<math><mi><mglyph></mglyph></mi></math>")]
        [DataRow("<math><annotation-xml encoding=\"text/html\"><textarea><!--</textarea><script>alert(1)</script>--></textarea></annotation-xml></math>", "<math><annotation-xml encoding=\"text/html\">--></textarea></annotation-xml></math>")]
        [DataRow("<math><annotation-xml encoding=\"TEXT/HTML\"><textarea><!--</textarea><script>alert(1)</script>--></textarea></annotation-xml></math>", "<math><annotation-xml encoding=\"TEXT/HTML\">--></textarea></annotation-xml></math>")]
        [DataRow("<math><annotation-xml encoding=\"image/svg+xml\"><title><img onerror=alert(1)></title></annotation-xml></math>", "<math><annotation-xml encoding=\"image/svg+xml\"></annotation-xml></math>")]
        [DataRow("<math><annotation-xml><svg><title><textarea><!--</textarea><script>alert(1)</script>--></textarea></title></svg></annotation-xml></math>", "<math><annotation-xml><svg></svg></annotation-xml></math>")]
        [DataRow("<svg><foreignObject><svg></svg><title><script>alert(1)</script></title></foreignObject></svg>", "<svg><foreignObject><svg></svg></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><svg><foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea></foreignObject></svg></foreignObject></svg>", "<svg><foreignObject><svg><foreignObject>--></textarea></foreignObject></svg></foreignObject></svg>")]
        [DataRow("<svg><g><title><script>alert(1)</script>", "<svg><g><title>")]
        [DataRow("<svg><g><style><img onerror=alert(1)>", "<svg><g><style><img >")]
        [DataRow("<select><xmp><!--</xmp><script>alert(1)</script>-->", "<select>-->")]
        [DataRow("<select><style><img onerror=alert(1)></style></select>", "<select></select>")]
        [DataRow("<select><noscript><!--</noscript><script>alert(1)</script>--></select>", "<select>--></select>")]
        [DataRow("<select><svg><title><script>alert(1)</script></title></svg></select>", "<select><svg></svg></select>")]
        [DataRow("<select><plaintext><script>alert(1)</script>", "<select><plaintext>")]
        [DataRow("<select></select><style><img src=x onerror=alert(1)></style>", "<select></select>")]
        [DataRow("<select><template></select></template><style></select><img src=x onerror=alert(1)></style>", "<select><template></select></template>")]
        [DataRow("<svg><![CDATA[ > </svg> ]]><textarea><img src=x onerror=alert(1)></textarea>", "<svg><![CDATA[ > </svg> ]]>")]
        [DataRow("<svg><![CDATA[ > </svg> ]]><title><script>alert(1)</script></title>", "<svg><![CDATA[ > </svg> ]]>")]
        [DataRow("<math><![CDATA[ > </math> ]]><style><img onerror=alert(1)></style>", "<math><![CDATA[ > </math> ]]>")]
        [DataRow("<svg><foreignObject><![CDATA[ > </foreignObject></svg> ]]></foreignObject></svg><noscript><img onerror=alert(1)></noscript>", "<svg><foreignObject><![CDATA[ > </foreignObject></svg> ]]></foreignObject></svg>")]
        public void RemovesRawTextElementWithMarkupWhereABrowserMayReadMarkup(string html, string expected) {
            Assert.AreEqual(expected, SaveFragment(html));
        }

        // Raw text without markup reads the same either way and is kept
        [TestMethod]
        [DataRow("<svg><title>a &lt; b</title></svg>")]
        [DataRow("<svg><style>g > rect { fill: red }</style></svg>")]
        [DataRow("<svg><foreignObject><textarea>x < 1</textarea></foreignObject></svg>")]
        [DataRow("<select><title>t</title></select>")]
        public void KeepsRawTextWithoutMarkupWhereABrowserMayReadMarkup(string html) {
            Assert.AreEqual(html, SaveFragment(html));
        }

        // Outside svg, math and select a browser reads title, style and textarea contents as text
        [TestMethod]
        [DataRow("<title><script>alert(1)</script></title>")]
        [DataRow("<textarea><script>alert(1)</script></textarea>")]
        [DataRow("<style><img src=x onerror=alert(1)></style>")]
        [DataRow("<svg></svg><title><script>alert(1)</script></title>")]
        [DataRow("<svg/><textarea><img src=x onerror=alert(1)></textarea>")]
        [DataRow("<svg><p><title><script>alert(1)</script></title></svg>")]
        [DataRow("<svg><font color=\"red\"><title><script>alert(1)</script></title></font></svg>")]
        [DataRow("<svg><g></p><title><script>alert(1)</script></title></svg>")]
        [DataRow("<svg><g></br><title><script>alert(1)</script></title></svg>")]
        [DataRow("</svg><title><script>alert(1)</script></title>")]
        [DataRow("<svg></svg></svg><textarea><script>alert(1)</script></textarea>")]
        [DataRow("a<![CDATA[ x ]]><textarea><img src=x onerror=alert(1)></textarea>")]
        [DataRow("<svg></svg><![CDATA[ > ]]><textarea><img src=x onerror=alert(1)></textarea>")]
        public void KeepsTitleAndTextareaTextOutsideSvgMathAndSelect(string html) {
            Assert.AreEqual(html, SaveFragment(html));
        }

        // A browser ignores the end tag of an svg or integration point while an HTML element is
        // open inside the integration point, and is in the svg again after both end. Where the
        // parser cannot tell if a browser closed an HTML element (implied end tags, formatting
        // elements opened again, a form end tag), it keeps it open and stays unsure.
        [TestMethod]
        [DataRow("<svg><foreignObject><div></svg></div></foreignObject><style><img src=x onerror=alert(1)></style></svg>", "<svg><foreignObject><div></svg></div></foreignObject></svg>")]
        [DataRow("<svg><desc><div></svg></div></desc><style><img src=x onerror=alert(1)></style></svg>", "<svg><desc><div></svg></div></desc></svg>")]
        [DataRow("<svg><foreignObject><section><div></svg></foreignObject></div></section></foreignObject><title><img onerror=alert(1)></title></svg>", "<svg><foreignObject><section><div></svg></foreignObject></div></section></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><span></svg></span></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject><span></svg></span></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><span></foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea>", "<svg><foreignObject><span></foreignObject>--></textarea>")]
        [DataRow("<svg><foreignObject><p><div></div></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject><p><div></div></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><li><div><li></li></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject><li><div><li></li></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><div><b></div><span></span></foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea>", "<svg><foreignObject><div><b></div><span></span></foreignObject>--></textarea>")]
        [DataRow("<svg><foreignObject><div><b></div><span></span></svg></b></foreignObject><style><img onerror=alert(1)></style>", "<svg><foreignObject><div><b></div><span></span></svg></b></foreignObject>")]
        [DataRow("<svg><foreignObject><form><span></form></svg><textarea>x</textarea></span></foreignObject><style><img onerror=alert(1)></style>", "<svg><foreignObject><form><span></form></svg><textarea>x</textarea></span></foreignObject>")]
        [DataRow("<svg><foreignObject><xmp></svg></xmp></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><noembed></svg></noembed></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><noframes></svg></noframes></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><iframe></svg></iframe></foreignObject><style><img onerror=alert(1)></style></svg>", "<svg><foreignObject></foreignObject></svg>")]
        [DataRow("<svg><foreignObject><plaintext></svg></plaintext></foreignObject><style><img src=x onerror=alert(1)></style>", "<svg><foreignObject><plaintext></svg></plaintext></foreignObject>")]
        [DataRow("<svg><foreignObject><div></svg><textarea><!--</textarea><script>alert(1)</script>--></textarea></div></foreignObject></svg>", "<svg><foreignObject><div></svg>--></textarea></div></foreignObject></svg>")]
        public void RemovesActiveContentAfterHTMLInIntegrationPoints(string html, string expected) {
            Assert.AreEqual(expected, SaveFragment(html));
        }

        // A browser ignores the slash of a self-closing title, style or textarea start tag read as
        // HTML, and reads xmp, noembed, noframes, iframe and (with scripting on) noscript contents
        // as text, so a comment start inside them hides nothing
        [TestMethod]
        [DataRow("<textarea/><!--</textarea><script>alert(1)</script>-->", "<textarea/><!--</textarea>-->")]
        [DataRow("<title/><!--</title><script>alert(1)</script>-->", "<title/><!--</title>-->")]
        [DataRow("<style/><!--</style><script>alert(1)</script>-->", "<style/><!--</style>-->")]
        [DataRow("<svg><textarea/><img onerror=alert(1)></svg>", "<svg><textarea/><img ></svg>")]
        [DataRow("<xmp><!--</xmp><script>alert(1)</script>-->", "<xmp><!--</xmp>-->")]
        [DataRow("<noembed><!--</noembed><script>alert(1)</script>-->", "<noembed><!--</noembed>-->")]
        [DataRow("<noframes><!--</noframes><script>alert(1)</script>-->", "<noframes><!--</noframes>-->")]
        [DataRow("<noscript><!--</noscript><script>alert(1)</script>-->", "<noscript><!--</noscript>-->")]
        [DataRow("<iframe><!--</iframe><script>alert(1)</script>-->", "-->")]
        public void RemovesScriptAfterRawTextElement(string html, string expected) {
            Assert.AreEqual(expected, SaveFragment(html));
        }

        // An active element read as raw text is removed with all the text the parser skipped, also
        // when its start tag is self-closing, and to the end of the page when its end tag can't be
        // read. An active element whose raw text never ends loses its start tag, and its contents
        // are read as markup.
        [TestMethod]
        [DataRow("<iframe/><script>alert(1)</script></iframe><p>a</p>", "<p>a</p>")]
        [DataRow("<iframe/src=x><script>alert(1)</script></iframe><p>a</p>", "<p>a</p>")]
        [DataRow("<iframe src=x><script>alert(1)</script>", "")]
        [DataRow("<iframe src=x><img src=x onerror=alert(1)>", "<img src=\"http://a.com/b/x\" >")]
        [DataRow("<iframe>x</iframe", "x</iframe")]
        [DataRow("<iframe>x</iframe a=\"b<script>alert(1)</script>", "")]
        [DataRow("<script>a<img src=x onerror=alert(1)>", "a<img src=\"http://a.com/b/x\" >")]
        public void RemovesActiveRawTextElementWithAllItsText(string html, string expected) {
            Assert.AreEqual(expected, SaveFragment(html));
        }

        // Text to a browser: noscript contents with scripting on. The parser goes on reading the
        // tags after it, also when no end tag ends it (then reading its contents as markup).
        [TestMethod]
        [DataRow("<noscript><img src=x onerror=alert(1)></noscript><a href=\"y\">", "<noscript><img src=x onerror=alert(1)></noscript><a href=\"http://a.com/b/y\">")]
        [DataRow("<xmp><img src=x onerror=alert(1)></xmp><a href=\"y\">", "<xmp><img src=x onerror=alert(1)></xmp><a href=\"http://a.com/b/y\">")]
        [DataRow("<noscript><img src=x onerror=alert(1)><a href=\"y\">", "<noscript><img src=\"http://a.com/b/x\" ><a href=\"http://a.com/b/y\">")]
        [DataRow("<xmp><img src=x onerror=alert(1)><a href=\"y\">", "<xmp><img src=\"http://a.com/b/x\" ><a href=\"http://a.com/b/y\">")]
        public void ReadsTheTagsAfterRawText(string html, string expected) {
            Assert.AreEqual(expected, SaveFragment(html));
        }

        // Plaintext has no end tag, so its contents are read as markup; a browser reads them as text
        [TestMethod]
        public void ReadsPlaintextContentsAsMarkup() {
            Assert.AreEqual("<plaintext></plaintext><img src=\"http://a.com/b/x\" >", SaveFragment("<plaintext><script>alert(1)</script></plaintext><img src=x onerror=alert(1)>"));
        }

        // An svg that stays open with many elements and end tags that close none of them is read
        // in time linear in its length, and so is raw text that no end tag ends
        [TestMethod]
        public void ManyUnmatchedEndTagsInSvgAreReadQuickly() {
            const int count = 50000;
            string[] pages = {
                "<svg>" + String.Concat(System.Linq.Enumerable.Repeat("<g>", count)) + String.Concat(System.Linq.Enumerable.Repeat("</x>", count)) + "<title><script>alert(1)</script>",
                "<svg><foreignObject>" + String.Concat(System.Linq.Enumerable.Repeat("<div>", count)) + String.Concat(System.Linq.Enumerable.Repeat("</svg>", count)) + "<textarea><script>alert(1)</script></textarea>",
                "<svg><foreignObject><span>" + String.Concat(System.Linq.Enumerable.Repeat("<b>", count)) + String.Concat(System.Linq.Enumerable.Repeat("</span>", count)) + "<textarea><script>alert(1)</script></textarea>",
                String.Concat(System.Linq.Enumerable.Repeat("<textarea><xmp><title>", count)) + "<script>alert(1)</script>"
            };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            string[] saved = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(pages, p => SaveFragment(p)));

            Assert.IsLessThan(5000L, stopwatch.ElapsedMilliseconds);
            foreach (string page in saved) {
                Assert.DoesNotContain("alert(1)", page);
            }
        }

        [TestMethod]
        public void VoidElementIgnoresStrayEndTag() {
            Assert.AreEqual("<p>a</p><p>b</p></embed>", SaveFragment("<embed src=\"x.swf\"><p>a</p><p>b</p></embed>"));
        }

        // The head start tag is replaced as a whole, so a newline right after it can't displace the policy
        [TestMethod]
        public void PolicyIsKeptNextToNewLines() {
            Assert.AreEqual("<head>" + General.ActiveContentPolicyMeta + "\n<title>t</title>\n</head>", Save("<head>\n<title>t</title>\n</head>"));
        }

        [TestMethod]
        public void KeepsAttributesTheSiteHelperReplaced() {
            const string html = "<a href=\"javascript:x\">x</a>";
            var replaces = new List<ReplaceInfo> {
                new ReplaceInfo { Offset = 3, Length = 19, Type = ReplaceType.ImageLinkHref, Value = "href=\"1.jpg\"" }
            };

            Assert.AreEqual("<a href=\"1.jpg\">x</a>", SaveFragment(html, replaces));
            // A second replacement at the same offset would make the result depend on sort order
            Assert.HasCount(1, replaces.Where(r => r.Offset == 3).ToList());
        }

        [TestMethod]
        public void KeepsOrdinaryMarkup() {
            const string html = "<body><div class=\"post\" id=\"p1\" data-md5=\"abc\"><a class=\"quotelink\" href=\"#p2\">&gt;&gt;2</a><img src=\"http://a.com/t.jpg\" alt=\"one\" style=\"width: 1px\"></div><noscript>enable</noscript><style>.a { color: red; }</style></body>";

            Assert.AreEqual(html, SaveFragment(html));
        }

        [TestMethod]
        public void DuplicateAttributesAreRecordedSeparately() {
            HTMLTag tag = new HTMLParser("<img src=a src=b alt=c>").FindStartTag("img");

            Assert.AreEqual("a", tag.GetAttributeValue("src"));
            CollectionAssert.AreEqual(new[] { "src", "alt" }, tag.Attributes.Select(a => a.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "b" }, tag.DuplicateAttributes.Select(a => a.Value).ToArray());
        }
    }
}
