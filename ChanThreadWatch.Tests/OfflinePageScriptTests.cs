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
