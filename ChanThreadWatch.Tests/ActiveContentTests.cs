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

        [TestMethod]
        public void RemovesScriptsWithTheirContents() {
            string saved = Save("<p>a</p><script src=\"s.js\"></script><SCRIPT type=\"text/javascript\">\nvar x = \"<b>\";\n</SCRIPT><p>b</p><svg><script>y()</script></svg>");

            Assert.AreEqual("<p>a</p><p>b</p><svg></svg>", saved);
        }

        [TestMethod]
        public void RemovesEmbeddedContent() {
            string saved = Save("<iframe src=\"https://x.com/\"><p>no frames</p></iframe>|<object data=\"x.swf\"><embed src=\"x.swf\"></object>|<embed src=\"y.swf\">|<applet code=\"A\"></applet>|<frameset><frame src=\"f.html\"></frameset>");

            Assert.AreEqual("||||<frameset></frameset>", saved);
        }

        [TestMethod]
        public void UnclosedScriptLosesOnlyItsStartTag() {
            Assert.AreEqual("<p>a</p>alert(1)", Save("<p>a</p><script>alert(1)"));
        }

        [TestMethod]
        public void RemovesEventHandlerAttributes() {
            string saved = Save("<body onload=\"init()\"><img src=\"http://a.com/i.jpg\" onerror=alert(1) ONCLICK='go()'><a href=\"#p1\" onmouseover=\"preview()\">&gt;&gt;1</a></body>");

            Assert.AreEqual("<body ><img src=\"http://a.com/i.jpg\"  ><a href=\"#p1\" >&gt;&gt;1</a></body>", saved);
        }

        // Browsers ignore a repeated attribute, so after the first is removed the second would apply
        [TestMethod]
        public void RemovesRepeatedEventHandlerAttributes() {
            Assert.AreEqual("<img   >", Save("<img onerror=a() onerror=b() onerror=c()>"));
        }

        [TestMethod]
        public void RemovesScriptURLAttributes() {
            string saved = Save("<a href=\"javascript:void(0)\">x</a><form action=\"vbscript:y\"></form><a href=\"#ok\" href=\"javascript:z\">y</a>");

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

        [TestMethod]
        public void FragmentGetsNoPolicy() {
            Assert.AreEqual("<p>a</p>", Save("<p>a</p>"));
        }

        // HTMLParser reads xmp contents as markup where a browser reads text, so the comment it sees
        // there hides this handler; only the policy blocks it
        [TestMethod]
        public void PolicyCoversMarkupTheParserMisreads() {
            string saved = Save("<head></head><body><xmp><!--</xmp><img src=x onerror=alert(1)>--></body>");

            Assert.AreEqual("<head>" + General.ActiveContentPolicyMeta + "</head><body><xmp><!--</xmp><img src=x onerror=alert(1)>--></body>", saved);
        }

        // Comments end where a browser ends them, so a script after a comment is found
        [TestMethod]
        [DataRow("<!--><script>alert(1)</script>", "<!-->")]
        [DataRow("<!---><script>alert(1)</script>-->", "<!--->-->")]
        [DataRow("<!----><script>alert(1)</script>", "<!---->")]
        [DataRow("<!-- a --!><script>alert(1)</script>-->", "<!-- a --!>-->")]
        [DataRow("<!-- a --><script>alert(1)</script>", "<!-- a -->")]
        public void RemovesScriptAfterComment(string html, string expected) {
            Assert.AreEqual(expected, Save(html));
        }

        // A browser reads these scripts as part of the comment, so they don't run
        [TestMethod]
        [DataRow("<!-- a -- ><script>alert(1)</script>-->")]
        [DataRow("<!-- a --!-><script>alert(1)</script>-->")]
        [DataRow("<!-- never closed <script>alert(1)</script>")]
        public void KeepsScriptTextInsideComment(string html) {
            Assert.AreEqual(html, Save(html));
        }

        // In svg and math a browser reads the tags in title, style and textarea, and in an
        // integration point (svg foreignObject, desc, title, MathML mi and an HTML annotation-xml)
        // it reads these elements as HTML text again, which can end before a comment does
        [TestMethod]
        [DataRow("<svg><title><script>alert(1)</script></title></svg>", "<svg><title></title></svg>")]
        [DataRow("<SVG><TITLE><script>alert(1)</script></TITLE></SVG>", "<SVG><TITLE></TITLE></SVG>")]
        [DataRow("<math><title><img onerror=alert(1)></title></math>", "<math><title><img ></title></math>")]
        [DataRow("<svg><style><script>alert(1)</script></style></svg>", "<svg><style></style></svg>")]
        [DataRow("<svg><font><title><script>alert(1)</script></title></font></svg>", "<svg><font><title></title></font></svg>")]
        [DataRow("<svg><g><textarea><img onerror=alert(1)></textarea></g></svg>", "<svg><g><textarea><img ></textarea></g></svg>")]
        [DataRow("<svg><foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea></foreignObject></svg>", "<svg><foreignObject><textarea><!--</textarea>--></textarea></foreignObject></svg>")]
        [DataRow("<svg><desc><style><!--</style><script>alert(1)</script>--></style></desc></svg>", "<svg><desc><style><!--</style>--></style></desc></svg>")]
        [DataRow("<math><mi><title><!--</title><script>alert(1)</script>--></title></mi></math>", "<math><mi><title><!--</title>--></title></mi></math>")]
        [DataRow("<math><mi><mglyph><title><img onerror=alert(1)></title></mglyph></mi></math>", "<math><mi><mglyph><title><img ></title></mglyph></mi></math>")]
        [DataRow("<math><annotation-xml encoding=\"text/html\"><textarea><!--</textarea><script>alert(1)</script>--></textarea></annotation-xml></math>", "<math><annotation-xml encoding=\"text/html\"><textarea><!--</textarea>--></textarea></annotation-xml></math>")]
        [DataRow("<math><annotation-xml><svg><title><textarea><!--</textarea><script>alert(1)</script>--></textarea></title></svg></annotation-xml></math>", "<math><annotation-xml><svg><title><textarea><!--</textarea>--></textarea></title></svg></annotation-xml></math>")]
        public void RemovesActiveContentInSvgAndMathText(string html, string expected) {
            Assert.AreEqual(expected, Save(html));
        }

        // Outside svg and math, and after an HTML element ends them, title and textarea hold text
        [TestMethod]
        [DataRow("<title><script>alert(1)</script></title>")]
        [DataRow("<textarea><script>alert(1)</script></textarea>")]
        [DataRow("<style><img src=x onerror=alert(1)></style>")]
        [DataRow("<svg></svg><title><script>alert(1)</script></title>")]
        [DataRow("<svg/><textarea><img src=x onerror=alert(1)></textarea>")]
        [DataRow("<svg><p><title><script>alert(1)</script></title></svg>")]
        [DataRow("<svg><font color=\"red\"><title><script>alert(1)</script></title></font></svg>")]
        [DataRow("<math><mi><title><script>alert(1)</script></title></mi></math>")]
        public void KeepsTitleAndTextareaTextOutsideSvgAndMath(string html) {
            Assert.AreEqual(html, Save(html));
        }

        [TestMethod]
        public void VoidElementIgnoresStrayEndTag() {
            Assert.AreEqual("<p>a</p><p>b</p></embed>", Save("<embed src=\"x.swf\"><p>a</p><p>b</p></embed>"));
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

            Assert.AreEqual("<a href=\"1.jpg\">x</a>", Save(html, replaces));
            // A second replacement at the same offset would make the result depend on sort order
            Assert.HasCount(1, replaces.Where(r => r.Offset == 3).ToList());
        }

        [TestMethod]
        public void KeepsOrdinaryMarkup() {
            const string html = "<body><div class=\"post\" id=\"p1\" data-md5=\"abc\"><a class=\"quotelink\" href=\"#p2\">&gt;&gt;2</a><img src=\"http://a.com/t.jpg\" alt=\"one\" style=\"width: 1px\"></div><noscript>enable</noscript><style>.a { color: red; }</style></body>";

            Assert.AreEqual(html, Save(html));
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
