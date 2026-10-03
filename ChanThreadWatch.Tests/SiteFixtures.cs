using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace JDP.Tests {
    // The sanitized real-markup fixtures in Fixtures/sites (made by tools/site-fixtures), their
    // manifest and allowlist, and the sterility check that keeps real data out of them.
    public static class SiteFixtures {
        public const string BaseURL = "http://fixture.test";
        public const string MediaURL = "http://media.test";

        public static string Directory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "sites");

        public static Dictionary<string, object> Allowlist => ReadJson("allowlist.json");

        public static Dictionary<string, object> Manifest => ReadJson("manifest.json");

        public static IEnumerable<object[]> Names => Manifest.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

        public static Dictionary<string, object> Entry(string name) => (Dictionary<string, object>)Manifest[name];

        // True if the value is a URL the sanitizer could have written
        public static bool IsPlaceholderURL(string value) => new SterilityCheck(Allowlist).IsAllowedURL(value);

        public static string ReadFixture(string name) => File.ReadAllText(Path.Combine(Directory, name + ".html"));

        // The MD5 the {{md5_N}} placeholder stands for. sanitize_site_fixture.py uses the same values.
        public static string PlaceholderMD5(int index) {
            using (MD5 md5 = MD5.Create()) {
                return Convert.ToBase64String(md5.ComputeHash(Encoding.ASCII.GetBytes("fixture-image-" + index)));
            }
        }

        public static string Substitute(string fixture) {
            return Substitute(fixture, BaseURL, MediaURL, PlaceholderMD5);
        }

        // md5 returns the base64 MD5 that {{md5_N}} stands for, given N; {{md5u_N}} gets the
        // URL-safe form of the same value without padding
        public static string Substitute(string fixture, string baseURL, string mediaURL, Func<int, string> md5) {
            string html = fixture.Replace("{{base}}", baseURL).Replace("{{media}}", mediaURL);
            html = Regex.Replace(html, @"\{\{md5_(\d+)\}\}", m => md5(Int32.Parse(m.Groups[1].Value)));
            return Regex.Replace(html, @"\{\{md5u_(\d+)\}\}", m => md5(Int32.Parse(m.Groups[1].Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        private static Dictionary<string, object> ReadJson(string fileName) {
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Directory, fileName)));
        }

        public static string[] Strings(object list) => ((IEnumerable)list).Cast<string>().ToArray();

        // Returns every piece of markup in the fixture that is not allowlisted or not a placeholder.
        // An empty list means the fixture is sterile.
        public static List<string> FindViolations(string fixture) {
            return new SterilityCheck(Allowlist).Run(fixture);
        }

        private sealed class SterilityCheck {
            private readonly HashSet<string> _tags;
            private readonly HashSet<string> _attributes;
            private readonly HashSet<string> _classes;
            private readonly Regex _id;
            private readonly Regex _url;
            private readonly Regex _text;
            private readonly Regex _fileName;
            private readonly Regex _md5;
            private readonly List<string> _violations = new List<string>();

            public SterilityCheck(Dictionary<string, object> allowlist) {
                var grammar = (Dictionary<string, object>)allowlist["grammar"];
                string number = (string)grammar["number"];
                string word = (string)grammar["word"];
                string fileName = (string)grammar["fileName"];
                string index = (string)grammar["index"];
                string keywords = String.Join("|", Strings(allowlist["urlKeywords"]).Concat(Strings(allowlist["fileExtensions"])).Select(Regex.Escape));
                string token = "(?:" + number + "|" + word + "|" + grammar["hex"] + "|" + grammar["md5UrlSafe"] + "|" + keywords + "|[-_./])";
                string prefixes = String.Join("|", Strings(allowlist["idPrefixes"]).Select(Regex.Escape));

                _tags = new HashSet<string>(Strings(allowlist["keptTags"]));
                _attributes = new HashSet<string>(Strings(allowlist["keptAttributes"]));
                _classes = new HashSet<string>(Strings(allowlist["keptClasses"]), StringComparer.Ordinal);
                _id = new Regex("^(?:" + prefixes + ")" + number + "$");
                _url = new Regex("^(?:" + grammar["host"] + ")?" + token + "*(?:#" + token + "*)?$");
                _fileName = new Regex("^" + fileName + "$");
                _md5 = new Regex("^" + grammar["md5"] + "$");
                _text = new Regex("^(?:Anonymous|name-" + index + "|!trip-" + index + "|(?:ID:)?id-" + index + "|" + fileName + "|>>" + number + "|>>>/" + word + "/" + number +
                    "|File: 10 KB, 64x64, " + fileName + "(?: <!-- " + grammar["md5"] + " -->)?)$");
            }

            // Tag offsets refer to the preprocessed HTML, where line breaks are "\n" also when the
            // file was checked out with CRLF
            public List<string> Run(string fixture) {
                var parser = new HTMLParser(fixture);
                fixture = parser.PreprocessedHTML;
                int textStart = 0;
                foreach (HTMLTag tag in parser.Tags) {
                    CheckText(fixture.Substring(textStart, tag.Offset - textStart));
                    CheckTag(tag);
                    textStart = tag.EndOffset;
                }
                CheckText(fixture.Substring(textStart));
                return _violations;
            }

            private void CheckTag(HTMLTag tag) {
                if (!_tags.Contains(tag.Name)) _violations.Add("tag <" + tag.Name + ">");
                if (tag.IsEnd && (tag.Attributes.Count != 0 || tag.DuplicateAttributes.Count != 0)) {
                    _violations.Add("attributes on </" + tag.Name + ">");
                    return;
                }
                foreach (HTMLAttribute attribute in tag.Attributes.Concat(tag.DuplicateAttributes)) {
                    CheckAttribute(tag, attribute);
                }
            }

            private void CheckAttribute(HTMLTag tag, HTMLAttribute attribute) {
                if (!_attributes.Contains(attribute.Name)) {
                    _violations.Add("attribute " + attribute.Name + " on <" + tag.Name + ">");
                    return;
                }
                if (!IsAllowedValue(attribute.Name, HttpUtility.HtmlDecode(attribute.Value))) {
                    _violations.Add(attribute.Name + "=\"" + attribute.Value + "\" on <" + tag.Name + ">");
                }
            }

            public bool IsAllowedURL(string value) => _url.IsMatch(value);

            private bool IsAllowedValue(string name, string value) {
                switch (name) {
                    case "class": return value.Split(' ').All(_classes.Contains);
                    case "id": return _id.IsMatch(value);
                    case "href":
                    case "src": return _url.IsMatch(value);
                    case "data-md5": return _md5.IsMatch(value);
                    case "title": return _fileName.IsMatch(value);
                    case "target": return value == "_blank";
                    default: return false;
                }
            }

            // Text between tags. The only markup allowed in it is the doctype at the start.
            private void CheckText(string text) {
                text = HttpUtility.HtmlDecode(text.Replace("<!DOCTYPE html>", "")).Trim();
                if (text.Length != 0 && !_text.IsMatch(text)) _violations.Add("text \"" + text + "\"");
            }
        }
    }
}
