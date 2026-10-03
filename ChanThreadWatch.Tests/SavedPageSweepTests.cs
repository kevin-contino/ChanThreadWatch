using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The last pass over a saved page (SavedPageSweep) works on the text alone, without
    // HTMLParser. These tests run it on raw inputs, so they hold even if HTMLParser's removal
    // missed everything.
    [TestClass]
    public class SavedPageSweepTests {
        private const string Page = "http://a.com/b/c";

        // Every way past HTMLParser that this change set found, and the forms of tags and
        // attributes the pass makes inert. Each comes out with its tag made text or its
        // attribute renamed.
        [TestMethod]
        [DataRow("<!---><script>alert(1)</script>-->", "<!--->&lt;script>alert(1)&lt;/script>-->")]
        [DataRow("<svg><title><script>alert(1)</script></title></svg>", "<svg><title>&lt;script>alert(1)&lt;/script></title></svg>")]
        [DataRow("<svg><title><img src=x onerror=alert(1)></title></svg>", "<svg><title><img src=x data-ctw-removed-onerror=alert(1)></title></svg>")]
        [DataRow("<textarea/><!--</textarea><script>alert(1)</script>-->", "<textarea/><!--</textarea>&lt;script>alert(1)&lt;/script>-->")]
        [DataRow("<xmp><!--</xmp><script>alert(1)</script>-->", "<xmp><!--</xmp>&lt;script>alert(1)&lt;/script>-->")]
        [DataRow("<div><svg><g></div><textarea><!--</textarea><img src=x onerror=alert(1)>-->", "<div><svg><g></div><textarea><!--</textarea><img src=x data-ctw-removed-onerror=alert(1)>-->")]
        [DataRow("<svg><foreignObject><div></svg></div></foreignObject><style><img src=x onerror=alert(1)></style>", "<svg><foreignObject><div></svg></div></foreignObject><style><img src=x data-ctw-removed-onerror=alert(1)></style>")]
        [DataRow("<iframe/><script>alert(1)</script></iframe>", "&lt;iframe/>&lt;script>alert(1)&lt;/script>&lt;/iframe>")]
        [DataRow("<iframe src=x><script>alert(1)</script>", "&lt;iframe src=x>&lt;script>alert(1)&lt;/script>")]
        [DataRow("<select><plaintext><script>alert(1)</script>", "<select><plaintext>&lt;script>alert(1)&lt;/script>")]
        [DataRow("<svg><foreignObject><span></foreignObject><textarea><!--</textarea><script>alert(1)</script>--></textarea>", "<svg><foreignObject><span></foreignObject><textarea><!--</textarea>&lt;script>alert(1)&lt;/script>--></textarea>")]
        [DataRow("<svg><foreignObject><div><b></div><span></span></svg></b></foreignObject><style><img onerror=alert(1)></style>", "<svg><foreignObject><div><b></div><span></span></svg></b></foreignObject><style><img data-ctw-removed-onerror=alert(1)></style>")]
        [DataRow("<svg><![CDATA[ > </svg> ]]><textarea><img src=x onerror=alert(1)></textarea>", "<svg><![CDATA[ > </svg> ]]><textarea><img src=x data-ctw-removed-onerror=alert(1)></textarea>")]
        [DataRow("<select><template></select></template><style></select><img src=x onerror=alert(1)></style>", "<select><template></select></template><style></select><img src=x data-ctw-removed-onerror=alert(1)></style>")]
        [DataRow("<!-- <a title=\"--><img src=x onerror=alert(1)>\">", "<!-- <a title=\"-->&lt;img src=x onerror=alert(1)>\">")]
        [DataRow("<a href=\"javascript:alert(1)\">x</a>", "<a data-ctw-removed-href=\"javascript:alert(1)\">x</a>")]
        [DataRow("<a href=\"&#106;avascript:alert(1)\">x</a>", "<a data-ctw-removed-href=\"&#106;avascript:alert(1)\">x</a>")]
        [DataRow("<form action=javascript:alert(1)><button formaction=\"JaVaScRiPt:alert(1)\">", "<form data-ctw-removed-action=javascript:alert(1)><button data-ctw-removed-formaction=\"JaVaScRiPt:alert(1)\">")]
        [DataRow("<math href=\"javascript:alert(1)\">x</math>", "<math data-ctw-removed-href=\"javascript:alert(1)\">x</math>")]
        [DataRow("<svg><a xlink:href=\"javascript:alert(1)\"><text>x</text></a></svg>", "<svg><a data-ctw-removed-xlink:href=\"javascript:alert(1)\"><text>x</text></a></svg>")]
        [DataRow("<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">", "<meta data-ctw-removed-http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">")]
        [DataRow("<META HTTP-EQUIV=\"Set-Cookie\" CONTENT=\"a=b\">", "<META data-ctw-removed-HTTP-EQUIV=\"Set-Cookie\" CONTENT=\"a=b\">")]
        [DataRow("<svg><animate attributeName=\"href\" to=\"javascript:alert(1)\"/></svg>", "<svg>&lt;animate attributeName=\"href\" data-ctw-removed-to=\"javascript:alert(1)\"/></svg>")]
        [DataRow("<svg><set attributeName=xlink:href values=\"x;javascript:alert(1)\"><a>x</a></svg>", "<svg>&lt;set attributeName=xlink:href values=\"x;javascript:alert(1)\"><a>x</a></svg>")]
        [DataRow("<img/onerror=alert(1) src=x>", "<img/data-ctw-removed-onerror=alert(1) src=x>")]
        [DataRow("<img src=\"x\"onerror=alert(1)>", "<img src=\"x\"data-ctw-removed-onerror=alert(1)>")]
        [DataRow("<img src=x onerror=\"alert(1)\" onerror=y>", "<img src=x data-ctw-removed-onerror=\"alert(1)\" data-ctw-removed-onerror=y>")]
        [DataRow("<base href=//example.invalid/>", "&lt;base href=//example.invalid/>")]
        [DataRow("<object data=x></object><embed src=x><frameset onload=alert(1)>", "&lt;object data=x>&lt;/object>&lt;embed src=x>&lt;frameset data-ctw-removed-onload=alert(1)>")]
        [DataRow("<SCRIPT >alert(1)</script >", "&lt;SCRIPT >alert(1)&lt;/script >")]
        [DataRow("<a title=\"<script>\">x</a>", "<a title=\"&lt;script>\">x</a>")]
        [DataRow("<img src=x\nonerror=alert(1)>", "<img src=x\ndata-ctw-removed-onerror=alert(1)>")]
        [DataRow("<img src=\"x onerror=alert(1)", "<img src=\"x onerror=alert(1)")]
        public void MakesActiveTagsAndAttributesInert(string html, string expected) {
            string swept = SavedPageSweep.Sweep(html);

            Assert.AreEqual(expected, swept);
            Assert.AreEqual(swept, SavedPageSweep.Sweep(swept), "a second pass changes nothing");
        }

        // Tag-like text in a comment, style or textarea is changed too, since the pass does not
        // follow how a browser reads the page. The changes make text, never new markup. A quote
        // in a comment can make the pass read the markup after it as an attribute value, whose
        // tags then become text.
        [TestMethod]
        [DataRow("<!-- <script>x</script> -->", "<!-- &lt;script>x&lt;/script> -->")]
        [DataRow("<style>/* <script> */</style>", "<style>/* &lt;script> */</style>")]
        [DataRow("<textarea><img src=x onerror=alert(1)></textarea>", "<textarea><img src=x data-ctw-removed-onerror=alert(1)></textarea>")]
        [DataRow("<!-- <a title=\"x --><p>a</p><p title=\"y\">b</p>", "<!-- <a title=\"x -->&lt;p>a&lt;/p>&lt;p title=\"y\">b</p>")]
        public void ChangesTagLikeTextAnywhere(string html, string expected) {
            Assert.AreEqual(expected, SavedPageSweep.Sweep(html));
        }

        // A policy can only restrict the page, so one the site wrote is kept
        [TestMethod]
        [DataRow("<meta http-equiv=\"Content-Security-Policy\" content=\"img-src 'self'\">")]
        [DataRow("<META HTTP-EQUIV=\"content-security-policy\" CONTENT=\"default-src 'self'\">")]
        public void KeepsSitePolicies(string html) {
            Assert.AreSame(html, SavedPageSweep.Sweep(html));
        }

        [TestMethod]
        public void KeepsOurScriptAndPolicies() {
            string html = "<html><head>" + OfflinePageScript.PolicyMeta + OfflinePageScript.CreateElement("4chan") + "</head><body>" +
                General.ActiveContentPolicyMeta + "</body></html>";

            Assert.AreSame(html, SavedPageSweep.Sweep(html));
        }

        // An element that looks like ours but is not exactly ours is made inert
        [TestMethod]
        public void MakesALookAlikeOfOurScriptInert() {
            string html = OfflinePageScript.CreateElement("4chan").Replace("// ", "alert(1); // ");

            StringAssert.StartsWith(SavedPageSweep.Sweep(html), "&lt;script data-site=\"4chan\">");
        }

        [TestMethod]
        public void KeepsAnOrdinaryPage() {
            const string html = "<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>a &lt; b</title>" +
                "<link rel=\"stylesheet\" href=\"a.css\"><style>p > a { color: red }</style></head>\n<body class=\"x\"><!-- c -->" +
                "<a href=\"1.jpg\" title='x'><img src=\"thumbs/1s.jpg\" alt=\"\" data-md5=\"abc==\"></a><br/><span>1 < 2</span>" +
                "<form action=\"post\"><input type=\"text\" name=\"online\" value=\"on=1\"></form></body></html>";

            Assert.AreSame(html, SavedPageSweep.Sweep(html));
        }

        [TestMethod]
        public void SweepsInLinearTime() {
            string[] pages = {
                "<a title=\"" + String.Concat(System.Linq.Enumerable.Repeat("<a ", 300000)),
                String.Concat(System.Linq.Enumerable.Repeat("<a b=", 300000)),
                new string('<', 1000000),
                String.Concat(System.Linq.Enumerable.Repeat("<script data-site=\"", 100000)),
                String.Concat(System.Linq.Enumerable.Repeat("<img onerror=x ", 200000))
            };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (string page in pages) {
                SavedPageSweep.Sweep(page);
            }

            Assert.IsLessThan(5000L, stopwatch.ElapsedMilliseconds);
        }

        private static string SaveThroughBothLayers(string html) {
            var replaces = new List<ReplaceInfo>();
            var htmlParser = new HTMLParser(html);
            General.AddOtherReplaces(htmlParser, Page, replaces);
            using (var writer = new StringWriter()) {
                General.WriteSavedPage(htmlParser.PreprocessedHTML, replaces, writer);
                return writer.ToString();
            }
        }

        // The removal and the last pass each keep these handlers from running; a page saved
        // through both has no event handler attribute or script tag left
        [TestMethod]
        [DataRow("<html><head></head><body><svg><![CDATA[ > </svg> ]]><textarea><img src=x onerror=alert(1)></textarea></body></html>")]
        [DataRow("<html><head></head><body><select><template></select></template><style></select><img src=x onerror=alert(1)></style></body></html>")]
        [DataRow("<html><head></head><body><div><svg><g></div><textarea><!--</textarea><img src=x onerror=alert(1)>--></body></html>")]
        public void SavedPageHasNoHandler(string html) {
            string saved = SaveThroughBothLayers(html);

            Assert.IsFalse(Regex.IsMatch(saved, "[\\s/\"']on[a-z]+\\s*=", RegexOptions.IgnoreCase), saved);
            Assert.IsFalse(Regex.IsMatch(saved, "<script", RegexOptions.IgnoreCase), saved);
            StringAssert.Contains(saved, General.ActiveContentPolicyMeta);
        }
    }
}
