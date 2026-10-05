using System;
using System.Collections.Generic;
using System.Text;

namespace JDP {
    // A last pass over the exact text of a saved page, after HTMLParser's removal, that does not
    // depend on how HTMLParser read the page. It reads every "<" that is followed by a letter (or
    // by "/" and a letter) as the start of a tag, wherever it is: in text, a comment, raw text or
    // a CDATA section. From there it reads the tag as a browser's tokenizer does, through quoted
    // attribute values, to the ">" that ends it (or the end of the page).
    // - A "<" inside such a tag is written as "&lt;". In an attribute value that a browser reads
    //   as one, it decodes to the same "<"; anywhere a browser reads text it is text. So every
    //   tag a browser can find in the saved page starts at a "<" that this pass read as a tag.
    // - A tag that can run code or load a page (script, iframe, frame, frameset, object, embed,
    //   applet, base, an svg animation of href) is made text by writing its "<" as "&lt;".
    // - An event handler attribute, an attribute whose value is a script URL, and the http-equiv
    //   of a meta refresh or set-cookie are renamed with RemovedAttributePrefix, which keeps the
    //   value and drops the effect. A policy meta is kept: it can only restrict the page, and a
    //   policy the site wrote stays as the site wrote it.
    // The pass only replaces "<" and adds letters and hyphens, so it makes no new markup, and a
    // second pass changes nothing. Our script element and policies are kept as they are.
    public static class SavedPageSweep {
        public const string RemovedAttributePrefix = "data-ctw-removed-";

        private const string LessThanText = "&lt;";

        private static readonly string[] _removedTagNames = {
            "script", "iframe", "frame", "frameset", "object", "embed", "applet", "base"
        };

        private static readonly string[] _animationNames = { "animate", "set", "animatemotion", "animatetransform" };

        private static readonly string[] _removedHttpEquivs = { "refresh", "set-cookie" };

        // An escape character has no use in a page. In a page a browser decodes as ISO-2022-JP it
        // switches character sets, so it is written as a replacement character.
        public static string Sweep(string html) {
            string text = html.IndexOf('\u001B') == -1 ? html : html.Replace('\u001B', '\uFFFD');
            return new Sweeper(text).Run();
        }

        private sealed class Edit {
            public int Offset;
            public int Length;
            public string Value;
        }

        private sealed class TagAttribute {
            public string Name;
            public string Value = String.Empty;
        }

        // A tag as the pass reads it: its name, its attributes, its end, and the edits inside it
        // in the order of their offsets
        private sealed class TagSpan {
            public int EndOffset;
            public string Name;
            public readonly List<TagAttribute> Attributes = new List<TagAttribute>();
            public readonly List<Edit> Edits = new List<Edit>();
        }

        private sealed class Sweeper {
            private readonly string _html;
            private readonly StringBuilder _output;
            private int _copied;
            private bool _isChanged;

            public Sweeper(string html) {
                _html = html;
                _output = new StringBuilder(html.Length);
            }

            public string Run() {
                int pos = 0;
                while ((pos = _html.IndexOf('<', pos)) != -1) {
                    pos = IsTagStart(pos) ? SweepTag(pos) : pos + 1;
                }
                if (!_isChanged) return _html;
                _output.Append(_html, _copied, _html.Length - _copied);
                return _output.ToString();
            }

            private bool IsTagStart(int pos) {
                int namePos = pos + 1 < _html.Length && _html[pos + 1] == '/' ? pos + 2 : pos + 1;
                return namePos < _html.Length && IsAsciiLetter(_html[namePos]);
            }

            // Returns the offset after the tag
            private int SweepTag(int pos) {
                int ownLength = GetOwnMarkupLength(pos);
                if (ownLength != 0) return pos + ownLength;
                TagSpan span = ReadTag(pos);
                if (IsRemovedTag(span)) span.Edits.Insert(0, new Edit { Offset = pos, Length = 1, Value = LessThanText });
                foreach (Edit edit in span.Edits) {
                    Apply(edit);
                }
                return span.EndOffset;
            }

            private void Apply(Edit edit) {
                _output.Append(_html, _copied, edit.Offset - _copied).Append(edit.Value);
                _copied = edit.Offset + edit.Length;
                _isChanged = true;
            }

            // Our script element and policies, which pass unchanged; 0 for anything else
            private int GetOwnMarkupLength(int pos) {
                foreach (string own in GetOwnMarkup(pos)) {
                    if (IsAt(pos, own)) return own.Length;
                }
                return 0;
            }

            private IEnumerable<string> GetOwnMarkup(int pos) {
                yield return General.ActiveContentPolicyMeta;
                yield return OfflinePageScript.PolicyMeta;
                string site = ReadScriptSite(pos);
                if (site != null) yield return OfflinePageScript.CreateElement(site);
            }

            private bool IsAt(int pos, string value) {
                return pos + value.Length <= _html.Length && String.CompareOrdinal(_html, pos, value, 0, value.Length) == 0;
            }

            // The site of an offline script start tag at pos, or null
            private string ReadScriptSite(int pos) {
                const string start = "<script data-site=\"";
                if (!IsAt(pos, start)) return null;
                int siteStart = pos + start.Length;
                int siteEnd = _html.IndexOf('"', siteStart, Math.Min(21, _html.Length - siteStart));
                if (siteEnd == -1 || siteEnd - siteStart > 20) return null;
                string site = _html.Substring(siteStart, siteEnd - siteStart);
                return IsSiteName(site) ? site : null;
            }

            private TagSpan ReadTag(int pos) {
                TagSpan span = new TagSpan();
                int nameStart = _html[pos + 1] == '/' ? pos + 2 : pos + 1;
                int nameEnd = ReadName(span, nameStart, false);
                span.Name = _html.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();
                span.EndOffset = ReadAttributes(span, nameEnd);
                return span;
            }

            // Reads a tag or attribute name to its end, noting any "<" in it
            private int ReadName(TagSpan span, int i, bool isAttributeName) {
                while (i < _html.Length && !IsNameEnd(_html[i], isAttributeName)) {
                    NoteLessThan(span, i);
                    i++;
                }
                return i;
            }

            private void NoteLessThan(TagSpan span, int i) {
                if (_html[i] == '<') span.Edits.Add(new Edit { Offset = i, Length = 1, Value = LessThanText });
            }

            // Returns the offset after the ">" that ends the tag, or the end of the page
            private int ReadAttributes(TagSpan span, int i) {
                while (i < _html.Length) {
                    char c = _html[i];
                    if (c == '>') return i + 1;
                    i = IsWhiteSpace(c) || c == '/' ? i + 1 : ReadAttribute(span, i);
                }
                return _html.Length;
            }

            // The first character of an attribute name may be "=", as for a browser. An attribute
            // to remove gets its rename before the edits inside it.
            private int ReadAttribute(TagSpan span, int i) {
                int editIndex = span.Edits.Count;
                NoteLessThan(span, i);
                int nameEnd = ReadName(span, i + 1, true);
                TagAttribute attribute = new TagAttribute { Name = _html.Substring(i, nameEnd - i).ToLowerInvariant() };
                span.Attributes.Add(attribute);
                int end = ReadAttributeValue(span, attribute, SkipWhiteSpace(nameEnd));
                if (IsRemovedAttribute(span, attribute)) span.Edits.Insert(editIndex, new Edit { Offset = i, Length = 0, Value = RemovedAttributePrefix });
                return end;
            }

            // Returns the offset after the value, or pos if there is none
            private int ReadAttributeValue(TagSpan span, TagAttribute attribute, int pos) {
                if (pos >= _html.Length || _html[pos] != '=') return pos;
                int i = SkipWhiteSpace(pos + 1);
                return IsQuote(i) ? ReadQuotedValue(span, attribute, i) : ReadUnquotedValue(span, attribute, i);
            }

            private bool IsQuote(int i) {
                return i < _html.Length && (_html[i] == '"' || _html[i] == '\'');
            }

            private int ReadUnquotedValue(TagSpan span, TagAttribute attribute, int i) {
                int valueEnd = i;
                while (valueEnd < _html.Length && !IsWhiteSpace(_html[valueEnd]) && _html[valueEnd] != '>') {
                    NoteLessThan(span, valueEnd);
                    valueEnd++;
                }
                attribute.Value = _html.Substring(i, valueEnd - i);
                return valueEnd;
            }

            // A value whose quote never closes runs to the end of the page
            private int ReadQuotedValue(TagSpan span, TagAttribute attribute, int i) {
                char quote = _html[i];
                int valueEnd = i + 1;
                while (valueEnd < _html.Length && _html[valueEnd] != quote) {
                    NoteLessThan(span, valueEnd);
                    valueEnd++;
                }
                attribute.Value = _html.Substring(i + 1, valueEnd - (i + 1));
                return Math.Min(valueEnd + 1, _html.Length);
            }

            private int SkipWhiteSpace(int i) {
                while (i < _html.Length && IsWhiteSpace(_html[i])) i++;
                return i;
            }
        }

        private static bool IsRemovedTag(TagSpan span) {
            if (Array.IndexOf(_removedTagNames, span.Name) != -1) return true;
            return Array.IndexOf(_animationNames, span.Name) != -1 && HasAttribute(span, "attributename", IsHrefName);
        }

        private static bool HasAttribute(TagSpan span, string name, Func<string, bool> isMatchingValue) {
            foreach (TagAttribute attribute in span.Attributes) {
                if (attribute.Name == name && isMatchingValue(attribute.Value)) return true;
            }
            return false;
        }

        private static bool IsRemovedHttpEquiv(TagSpan span, TagAttribute attribute) {
            if (span.Name != "meta" || attribute.Name != "http-equiv") return false;
            return Array.IndexOf(_removedHttpEquivs, General.DecodeAttributeValue(attribute.Value).Trim().ToLowerInvariant()) != -1;
        }

        private static bool IsHrefName(string value) {
            string name = General.DecodeAttributeValue(value).Trim().ToLowerInvariant();
            return name == "href" || name == "xlink:href";
        }

        private static bool IsRemovedAttribute(TagSpan span, TagAttribute attribute) {
            if (attribute.Name.StartsWith(RemovedAttributePrefix, StringComparison.Ordinal)) return false;
            return attribute.Name.StartsWith("on", StringComparison.Ordinal) || General.IsScriptURL(attribute.Value) || IsRemovedHttpEquiv(span, attribute);
        }

        private static bool IsNameEnd(char c, bool isAttributeName) {
            return IsWhiteSpace(c) || c == '/' || c == '>' || (isAttributeName && c == '=');
        }

        private static bool IsSiteName(string site) {
            if (site.Length == 0) return false;
            foreach (char c in site) {
                if (!IsLowerAsciiLetterOrDigit(c)) return false;
            }
            return true;
        }

        private static bool IsWhiteSpace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        private static bool IsAsciiLetter(char c) {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsLowerAsciiLetterOrDigit(char c) {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }
    }
}
