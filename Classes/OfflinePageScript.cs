using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JDP {
    // Our own script for saved thread pages (Resources\OfflinePageScript.js): quote previews,
    // backlinks and inline image expansion that work offline. The site's scripts are never kept;
    // the page's policy allows only this script, by the hash of its exact text.
    public static class OfflinePageScript {
        // The site markup the script reads, chosen by the site helper
        public const string FourChan = "4chan";
        public const string Vichan = "vichan";
        public const string Fuuka = "fuuka";
        public const string FoolFuuka = "foolfuuka";
        public const string LynxChan = "lynxchan";

        private const string ResourceName = "OfflinePageScript.js";

        // Browsers hash the script text after turning CRLF into LF, so the text is kept with LF
        // whatever line endings the resource was built with
        public static readonly string Text = LoadText();

        public static readonly string Hash = ComputeHash(Text);

        // Like General.ActiveContentPolicyMeta, but allows this script, and no requests from it
        public static readonly string PolicyMeta = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'sha256-" + Hash + "'; object-src 'none'; frame-src 'none'; connect-src 'none'\">";

        public static string CreateElement(string site) {
            if (!Regex.IsMatch(site, "^[a-z0-9]+$")) throw new ArgumentException("Invalid site name: " + site, nameof(site));
            return "<script data-site=\"" + site + "\">" + Text + "</script>";
        }

        // Returns the replacement that adds the policy and the script to the head, or null if the
        // page has no head that the browser would see as such.
        // The browser only reads a charset declaration in the first 1024 bytes, and a saved page
        // opened from disk has no other, so the policy and the script go right after the head's
        // charset declaration when only head content comes before it. That declaration is written
        // again without its other attributes. Otherwise they go right after the head start tag.
        public static ReplaceInfo CreateHeadReplace(HTMLParser htmlParser, string site) {
            int anchorIndex = FindHeadAnchorIndex(htmlParser);
            if (anchorIndex == -1) return null;
            string headStart = PolicyMeta + CreateElement(site);
            int charsetIndex = FindHeadCharsetIndex(htmlParser, anchorIndex);
            if (charsetIndex != -1) {
                HTMLTag meta = htmlParser.Tags[charsetIndex];
                return CreateTagReplace(meta, "<meta charset=\"" + GetMetaCharset(meta) + "\">" + headStart);
            }
            HTMLTag anchor = htmlParser.Tags[anchorIndex];
            return CreateTagReplace(anchor, "<" + anchor.Name + ">" + headStart);
        }

        private static ReplaceInfo CreateTagReplace(HTMLTag tag, string value) {
            return new ReplaceInfo {
                Offset = tag.Offset,
                Length = tag.Length,
                Type = ReplaceType.Other,
                Value = value
            };
        }

        // Returns the index of the head start tag, or of the html start tag when the head is
        // implied, or -1. Only the first tags of the page count: a head start tag after other
        // content (a body start tag, text, or noscript, template, svg or any other element that
        // the browser may read as text or as foreign content) does not start the browser's head.
        private static int FindHeadAnchorIndex(HTMLParser htmlParser) {
            if (IsLeadingStartTag(htmlParser, 0, "head")) return 0;
            if (!IsLeadingStartTag(htmlParser, 0, "html")) return -1;
            return IsLeadingStartTag(htmlParser, 1, "head") ? 1 : 0;
        }

        private static bool IsLeadingStartTag(HTMLParser htmlParser, int index, string name) {
            IList<HTMLTag> tags = htmlParser.Tags;
            return index < tags.Count && !tags[index].IsEnd && tags[index].NameEquals(name) && IsBlankBefore(htmlParser, index);
        }

        // White space, a BOM, a plain doctype, and comments that end where both HTMLParser and a
        // browser end them. A browser ends "<!-->" and "<!--->" right away and a comment at "--!>",
        // where HTMLParser may read on to a later "-->" and miss the tags in between, so such
        // comments, a comment inside a comment, and any other markup do not count as blank.
        private static readonly Regex _blankMarkup = new Regex("^(?:\\s|\\uFEFF|<!doctype[^<>]*>|<!--(?!>|->)(?:(?!--!>|<!--|-->)[\\s\\S])*-->)*$", RegexOptions.IgnoreCase);

        // True if only white space, well-formed comments and the doctype come between the tag and
        // the tag before it
        private static bool IsBlankBefore(HTMLParser htmlParser, int index) {
            int start = index > 0 ? htmlParser.Tags[index - 1].EndOffset : 0;
            return _blankMarkup.IsMatch(htmlParser.PreprocessedHTML.Substring(start, htmlParser.Tags[index].Offset - start));
        }

        // Tags that keep the browser in the head
        private static readonly string[] _headContentTags = { "meta", "link", "base", "title", "style", "script" };

        // Returns the index of the first charset declaration after the anchor that only head
        // content comes before, or -1
        private static int FindHeadCharsetIndex(HTMLParser htmlParser, int anchorIndex) {
            IList<HTMLTag> tags = htmlParser.Tags;
            for (int i = anchorIndex + 1; i < tags.Count && IsHeadContent(htmlParser, i); i++) {
                if (GetMetaCharset(tags[i]) != null) return i;
            }
            return -1;
        }

        // The text of a title, style or script is not content of its own
        private static bool IsHeadContent(HTMLParser htmlParser, int index) {
            HTMLTag previous = htmlParser.Tags[index - 1];
            bool previousHoldsText = !previous.IsEnd && previous.NameEqualsAny("title", "style", "script");
            return htmlParser.Tags[index].NameEqualsAny(_headContentTags) && (previousHoldsText || IsBlankBefore(htmlParser, index));
        }

        private static readonly Regex _charsetLabel = new Regex("^[A-Za-z0-9._:-]{1,40}$");

        // The charset of a meta charset or http-equiv content-type declaration, or null
        private static string GetMetaCharset(HTMLTag tag) {
            if (tag.IsEnd || !tag.NameEquals("meta")) return null;
            string charset = tag.GetAttributeValue("charset") ?? GetContentTypeCharset(tag);
            return IsCharsetLabel(charset) ? charset.Trim() : null;
        }

        private static bool IsCharsetLabel(string charset) {
            return charset != null && _charsetLabel.IsMatch(charset.Trim());
        }

        private static string GetContentTypeCharset(HTMLTag tag) {
            if (!String.Equals(tag.GetAttributeValue("http-equiv"), "Content-Type", StringComparison.OrdinalIgnoreCase)) return null;
            Match match = Regex.Match(tag.GetAttributeValueOrEmpty("content"), "charset\\s*=\\s*[\"']?([^\"';\\s]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string LoadText() {
            using (Stream stream = typeof(OfflinePageScript).Assembly.GetManifestResourceStream(ResourceName)) {
                if (stream == null) throw new InvalidOperationException("Missing resource " + ResourceName);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
                    return reader.ReadToEnd().Replace("\r\n", "\n");
                }
            }
        }

        private static string ComputeHash(string text) {
            using (SHA256 sha256 = SHA256.Create()) {
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }
    }
}
