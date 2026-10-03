using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Saved pages of known sites get our own offline script (Resources\OfflinePageScript.js), and
    // a policy that allows only that script, by its hash. The site's own scripts stay removed.
    [TestClass]
    public class OfflinePageScriptTests {
        private const string Page = "http://a.com/b/c";

        private static readonly Regex _scriptElement = new Regex("<script data-site=\"([a-z0-9]+)\">(.*?)</script>", RegexOptions.Singleline);
        private static readonly Regex _policyHash = new Regex("<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'sha256-([A-Za-z0-9+/=]+)'; object-src 'none'; frame-src 'none'; connect-src 'none'\">");

        private static string Save(string html, string site) {
            var replaces = new List<ReplaceInfo>();
            var htmlParser = new HTMLParser(html);
            General.AddOtherReplaces(htmlParser, Page, replaces, site);
            using (var writer = new StringWriter()) {
                General.WriteReplacedString(htmlParser.PreprocessedHTML, replaces, writer);
                return writer.ToString();
            }
        }

        // The overload that existing callers use
        private static string SaveWithoutSite(string html) {
            var replaces = new List<ReplaceInfo>();
            var htmlParser = new HTMLParser(html);
            General.AddOtherReplaces(htmlParser, Page, replaces);
            using (var writer = new StringWriter()) {
                General.WriteReplacedString(htmlParser.PreprocessedHTML, replaces, writer);
                return writer.ToString();
            }
        }

        // The base64 SHA-256 a browser computes for the text of an inline script
        public static string HashOf(string scriptText) {
            using (SHA256 sha256 = SHA256.Create()) {
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(scriptText)));
            }
        }

        // Checks that the saved page has exactly one script, ours for the site, and a policy
        // whose hash is the hash of that script's exact text
        public static void AssertHasOfflineScript(string savedPage, string site, string message = "") {
            MatchCollection scripts = _scriptElement.Matches(savedPage);
            Assert.HasCount(1, scripts, message);
            Assert.AreEqual(1, Regex.Matches(savedPage, "<script", RegexOptions.IgnoreCase).Count, message + " site scripts");
            Assert.AreEqual(site, scripts[0].Groups[1].Value, message);
            Assert.AreEqual(OfflinePageScript.Text, scripts[0].Groups[2].Value, message);
            Match policy = _policyHash.Match(savedPage);
            Assert.IsTrue(policy.Success, message + " policy");
            Assert.AreEqual(HashOf(scripts[0].Groups[2].Value), policy.Groups[1].Value, message + " hash");
            Assert.IsLessThan(scripts[0].Index, policy.Index, message + " the policy comes first");
            Assert.DoesNotContain(General.ActiveContentPolicyMeta, savedPage, message);
        }

        [TestMethod]
        [DataRow(typeof(FourChanSiteHelper), "4chan")]
        [DataRow(typeof(FourChanLookAlikeSiteHelper), "4chan")]
        [DataRow(typeof(InfinitechanSiteHelper), "vichan")]
        [DataRow(typeof(FuukaSiteHelper), "fuuka")]
        [DataRow(typeof(FoolFuukaSiteHelper), "foolfuuka")]
        [DataRow(typeof(LynxChanSiteHelper), "lynxchan")]
        public void SupportedSiteHelperChoosesItsScriptSite(Type helperType, string site) {
            var helper = (SiteHelper)Activator.CreateInstance(helperType);

            Assert.AreEqual(site, helper.GetOfflinePageScriptSite());
            AssertHasOfflineScript(Save("<html><head><title>t</title></head><body></body></html>", helper.GetOfflinePageScriptSite()), site);
        }

        [TestMethod]
        [DataRow("example.com")]
        [DataRow("unknown.org")]
        public void GenericSiteHelperGetsNoScript(string host) {
            SiteHelper helper = SiteHelpers.GetInstance(host);

            Assert.AreEqual(typeof(SiteHelper), helper.GetType());
            Assert.IsNull(helper.GetOfflinePageScriptSite());
        }

        [TestMethod]
        public void PageWithoutScriptKeepsTheOldPolicy() {
            const string html = "<html><head><title>t</title></head><body></body></html>";
            string saved = Save(html, null);

            Assert.AreEqual("<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'none'; object-src 'none'; frame-src 'none'\"><title>t</title></head><body></body></html>", saved);
            Assert.AreEqual(saved, SaveWithoutSite(html));
        }

        [TestMethod]
        public void ScriptAndItsPolicyAreAddedToTheHead() {
            string saved = Save("<html><head lang=\"en\"><title>t</title></head><body></body></html>", "4chan");

            Assert.AreEqual("<html><head>" + OfflinePageScript.PolicyMeta + OfflinePageScript.CreateElement("4chan") + "<title>t</title></head><body></body></html>", saved);
        }

        [TestMethod]
        public void PageWithImpliedHeadGetsScriptAfterHtmlTag() {
            Assert.AreEqual("<html>" + OfflinePageScript.PolicyMeta + OfflinePageScript.CreateElement("fuuka") + "<p>a</p></html>", Save("<html lang=\"en\"><p>a</p></html>", "fuuka"));
        }

        [TestMethod]
        public void FragmentGetsNoScript() {
            Assert.AreEqual("<p>a</p>", Save("<p>a</p>", "4chan"));
        }

        [TestMethod]
        public void SiteScriptsAreRemovedAndOursIsKept() {
            const string html = "<html><head><script src=\"site.js\"></script><script>var board = 'g';</script></head>" +
                "<body onload=\"init()\"><a href=\"javascript:quote(1)\">1</a><script>alert(1)</script><svg><script>x()</script></svg></body></html>";

            string saved = Save(html, "4chan");

            AssertHasOfflineScript(saved, "4chan");
            Assert.DoesNotContain("site.js", saved);
            Assert.DoesNotContain("var board", saved);
            Assert.DoesNotContain("alert(1)", saved);
            Assert.DoesNotContain("x()", saved);
            Assert.DoesNotContain("onload", saved);
            Assert.DoesNotContain("javascript:", saved);
        }

        // A reparse reads a saved page again: our old script is removed like any other and a new one added
        [TestMethod]
        public void SavingASavedPageAgainKeepsOneScript() {
            string saved = Save(Save("<html><head></head><body></body></html>", "lynxchan"), "lynxchan");

            Assert.HasCount(1, _scriptElement.Matches(saved));
            AssertHasOfflineScript(saved, "lynxchan");
        }

        private static int CountPolicies(string html) {
            return Regex.Matches(html, "http-equiv=\"Content-Security-Policy\"", RegexOptions.IgnoreCase).Count;
        }

        // Every policy applies, so one left from the last save would block the new script
        [TestMethod]
        [DataRow("4chan")]
        [DataRow(null)]
        public void SavingTwiceLeavesOnePolicy(string site) {
            const string html = "<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>t</title>\n</head>\n<body></body>\n</html>";

            string saved = Save(Save(Save(html, site), site), site);

            Assert.AreEqual(1, CountPolicies(saved));
            if (site != null) AssertHasOfflineScript(saved, site);
        }

        // A page saved before the script existed has the policy that allows no script
        [TestMethod]
        [DataRow("<html><head>" + General.ActiveContentPolicyMeta + "<title>t</title></head><body></body></html>")]
        [DataRow("<html><head>" + General.ActiveContentPolicyMeta + General.ActiveContentPolicyMeta + "\n<meta charset=\"utf-8\"></head><body></body></html>")]
        [DataRow("<html><head><META HTTP-EQUIV=\"content-security-policy\" CONTENT=\"script-src 'none'; object-src 'none'; frame-src 'none'\"></head></html>")]
        public void PageSavedWithTheOldPolicyGetsOnlyTheNewOne(string html) {
            string saved = Save(html, "fuuka");

            Assert.AreEqual(1, CountPolicies(saved));
            AssertHasOfflineScript(saved, "fuuka");
        }

        [TestMethod]
        public void PageThatGetsNoNewPolicyKeepsItsEarlierOne() {
            const string html = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'none'; object-src 'none'; frame-src 'none'\"><p>a</p>";

            foreach (string site in new[] { null, "4chan" }) {
                Assert.AreEqual(html, Save(html, site), site);
            }
        }

        [TestMethod]
        public void PolicyThatTheSiteWroteIsKept() {
            const string sitePolicy = "<meta http-equiv=\"Content-Security-Policy\" content=\"img-src 'self'\">";
            const string otherHash = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'sha256-abc='; object-src 'none'; frame-src 'none'; connect-src 'none'\">";

            string saved = Save("<html><head>" + sitePolicy + otherHash + "</head><body></body></html>", "4chan");

            StringAssert.Contains(saved, sitePolicy);
            StringAssert.Contains(saved, otherHash);
            Assert.AreEqual(3, CountPolicies(saved));
        }

        // A page opened from disk has no HTTP charset, and the browser only looks for a charset
        // declaration in the first 1024 bytes
        [TestMethod]
        [DataRow("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>t</title>", "utf-8")]
        [DataRow("<html><head><title>t</title><link rel=\"stylesheet\" href=\"a.css\"><meta http-equiv=\"Content-Type\" content=\"text/html; charset=Shift_JIS\">", "Shift_JIS")]
        [DataRow("<html><head><script>var a = '<meta>';</script><style>p { }</style><meta name=\"x\" content=\"y\"><meta charset=UTF-8 onload=\"x()\">", "UTF-8")]
        [DataRow("<html><title>t</title><meta charset=\"windows-1252\">", "windows-1252")]
        public void CharsetDeclarationStaysInTheFirst1024Bytes(string head, string charset) {
            string html = head + "</head><body><p>a</p></body></html>";

            foreach (string saved in new[] { Save(html, "4chan"), Save(Save(html, "4chan"), "4chan") }) {
                byte[] bytes = Encoding.UTF8.GetBytes(saved);
                string start = Encoding.ASCII.GetString(bytes, 0, Math.Min(1024, bytes.Length));
                StringAssert.Contains(start, "<meta charset=\"" + charset + "\">");
                Assert.IsLessThan(saved.IndexOf("<script data-site", StringComparison.Ordinal), saved.IndexOf("Content-Security-Policy", StringComparison.Ordinal));
                Assert.DoesNotContain("onload", saved);
                AssertHasOfflineScript(saved, "4chan");
            }
        }

        // A charset declaration after other content, or one with a label that is not a charset
        // name, is not moved; the script then goes right after the head start tag
        [TestMethod]
        [DataRow("<html><head><title>t</title><p>x</p><meta charset=\"utf-8\"></head></html>")]
        [DataRow("<html><head><noscript></noscript><meta charset=\"utf-8\"></head></html>")]
        [DataRow("<html><head><meta charset=\"utf-8&quot;&gt;\"></head></html>")]
        public void CharsetDeclarationThatIsNotInTheHeadIsLeft(string html) {
            string saved = Save(html, "4chan");

            StringAssert.StartsWith(saved, "<html><head>" + OfflinePageScript.PolicyMeta);
            AssertHasOfflineScript(saved, "4chan");
        }

        // The browser's head starts at the first tags of the page only. A head start tag that the
        // parser finds elsewhere is not the head: the page then gets the policy that allows no
        // script where it always did, and no script.
        [TestMethod]
        [DataRow("<noscript><head></head></noscript><p>a</p>")]
        [DataRow("<noembed><head></head></noembed>")]
        [DataRow("<xmp><head></head></xmp>")]
        [DataRow("<template><head></head></template>")]
        [DataRow("<svg><head></head></svg>")]
        [DataRow("<math><head></head></math>")]
        [DataRow("<body><head></head></body>")]
        [DataRow("text<html><head></head></html>")]
        [DataRow("<p>a</p><html><head></head></html>")]
        [DataRow("<!-- <head> --><p>a</p>")]
        // A browser ends these comments earlier than HTMLParser does
        [DataRow("<!---><html><head></head></html>")]
        [DataRow("<!--><html><head></head></html>")]
        [DataRow("<!-- a --!> <html><head></head></html>")]
        [DataRow("<!-- a <!-- b --><html><head></head></html>")]
        [DataRow("<!---><body><script>alert(1)</script>--><html><head><title>t</title></head><body></body></html>")]
        [DataRow("<?xml version=\"1.0\"?><html><head></head></html>")]
        public void HeadThatTheBrowserDoesNotSeeGetsNoScript(string html) {
            string saved = Save(html, "4chan");

            Assert.DoesNotContain("<script data-site", saved);
            Assert.DoesNotContain(OfflinePageScript.PolicyMeta, saved);
            Assert.AreEqual(SaveWithoutSite(html), saved);
        }

        [TestMethod]
        [DataRow("<!DOCTYPE html>\n<!-- c -->\n<html>\n<!-- d -->\n<head>\n<title>t</title></head></html>", "<head>")]
        [DataRow("\uFEFF<html><head></head></html>", "<head>")]
        [DataRow("<HEAD><title>t</title></HEAD><body></body>", "<head>")]
        [DataRow("<html><title>t</title></html>", "<html>")]
        [DataRow("<html>\n<svg><head></head></svg></html>", "<html>")]
        [DataRow("<html><body><head></head></body></html>", "<html>")]
        public void HeadAtTheStartOfThePageGetsTheScript(string html, string anchor) {
            string saved = Save(html, "4chan");

            StringAssert.Contains(saved, anchor + OfflinePageScript.PolicyMeta);
            AssertHasOfflineScript(saved, "4chan");
        }

        // HTMLParser reads "<!--->" as the start of a comment that ends at the next "-->", where a
        // browser ends it at once and runs the script. Only the policy keeps it from running; this
        // documents HTMLParser's behavior, which a separate change is to fix.
        [TestMethod]
        public void CommentThatABrowserEndsEarlyHidesAScriptFromTheRemoval() {
            string saved = Save("<html><head></head><body><!---><script>alert(1)</script>--></body></html>", "4chan");

            StringAssert.Contains(saved, "<!---><script>alert(1)</script>-->");
            StringAssert.StartsWith(saved, "<html><head>" + OfflinePageScript.PolicyMeta);
        }

        // HTMLParser reads an svg title as text, so it finds no script tag there; a browser runs the
        // svg script. Only the policy, which allows no script but ours, keeps it from running.
        [TestMethod]
        public void SvgTitleScriptIsNotRemovedButThePolicyBlocksIt() {
            string saved = Save("<html><head></head><body><svg><title><script>alert(1)</script></title></svg></body></html>", "4chan");

            StringAssert.Contains(saved, "<svg><title><script>alert(1)</script></title></svg>");
            StringAssert.StartsWith(saved, "<html><head>" + OfflinePageScript.PolicyMeta);
        }

        [TestMethod]
        public void PolicyHashIsTheHashOfTheScriptText() {
            Assert.AreEqual(HashOf(OfflinePageScript.Text), OfflinePageScript.Hash);
            StringAssert.Contains(OfflinePageScript.PolicyMeta, "script-src 'sha256-" + HashOf(OfflinePageScript.Text) + "'");
            StringAssert.Contains(OfflinePageScript.PolicyMeta, "object-src 'none'; frame-src 'none'; connect-src 'none'");
        }

        // Browsers hash the text after turning CRLF into LF and decoding it with the page's
        // encoding, so the text must have no CR and only ASCII. With no less-than sign it can't
        // end its element or be read as markup by any parser.
        [TestMethod]
        public void ScriptTextIsSafeToInsertIntoAnyPage() {
            string text = OfflinePageScript.Text;

            Assert.IsGreaterThan(1000, text.Length);
            Assert.DoesNotContain("\r", text);
            Assert.DoesNotContain("<", text);
            Assert.IsTrue(Regex.IsMatch(text, "^[\\x09\\x0A\\x20-\\x7E]*$"), "ASCII only");
        }

        // The script must not run code from strings, make requests, store data or navigate
        [TestMethod]
        [DataRow("eval")]
        [DataRow("Function(")]
        [DataRow("innerHTML")]
        [DataRow("outerHTML")]
        [DataRow("insertAdjacentHTML")]
        [DataRow("document.write")]
        [DataRow("setTimeout")]
        [DataRow("setInterval")]
        [DataRow("fetch")]
        [DataRow("XMLHttpRequest")]
        [DataRow("WebSocket")]
        [DataRow("sendBeacon")]
        [DataRow("Storage")]
        [DataRow("indexedDB")]
        [DataRow("cookie")]
        [DataRow("location")]
        [DataRow("window.open")]
        [DataRow("import(")]
        public void ScriptTextAvoidsUnsafeAPIs(string api) {
            Assert.DoesNotContain(api, OfflinePageScript.Text);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("4chan\" onload=\"x()")]
        [DataRow("Vichan")]
        public void InvalidSiteNameIsRejected(string site) {
            Assert.ThrowsExactly<ArgumentException>(() => OfflinePageScript.CreateElement(site));
        }
    }
}
