using System;
using System.Collections.Generic;

namespace JDP {
    public class HTMLParser {
        private string _preprocessedHTML;
        private List<HTMLTag> _tags;
        private Dictionary<int, int> _offsetToIndex = new Dictionary<int, int>();

        public HTMLParser(string html) {
            _preprocessedHTML = Preprocess(html);
            _tags = new List<HTMLTag>(ParseTags(_preprocessedHTML, 0, _preprocessedHTML.Length));
            for (int i = 0; i < _tags.Count; i++) {
                _offsetToIndex.Add(_tags[i].Offset, i);
            }
        }

        public string PreprocessedHTML {
            get { return _preprocessedHTML; }
        }

        public IList<HTMLTag> Tags {
            get { return _tags.AsReadOnly(); }
        }

        public string GetHTML(HTMLTag startTag, HTMLTag endTag) {
            return GetSection(_preprocessedHTML, startTag.Offset, startTag.IsSelfClosing ? startTag.EndOffset : endTag.EndOffset);
        }

        public string GetHTML(HTMLTagRange tagRange) {
            return GetHTML(tagRange.StartTag, tagRange.EndTag);
        }

        public string GetInnerHTML(HTMLTag startTag, HTMLTag endTag) {
            return startTag.IsSelfClosing ? String.Empty : GetSection(_preprocessedHTML, startTag.EndOffset, endTag.Offset);
        }

        public string GetInnerHTML(HTMLTagRange tagRange) {
            return GetInnerHTML(tagRange.StartTag, tagRange.EndTag);
        }

        public IEnumerable<HTMLTag> EnumerateTags(HTMLTag startAfterTag, HTMLTag stopBeforeTag) {
            int startIndex = startAfterTag != null ? (GetTagIndex(startAfterTag) + 1) : 0;
            int stopIndex = GetStopIndex(stopBeforeTag);
            for (int i = startIndex; i <= stopIndex; i++) {
                yield return _tags[i];
            }
        }

        public IEnumerable<HTMLTag> EnumerateTags(HTMLTagRange containingTagRange) {
            return EnumerateTags(containingTagRange.StartTag, containingTagRange.EndTag);
        }

        public HTMLTag FindTagById(string id) {
            foreach (HTMLTag tag in EnumerateTags(null, null)) {
                if (tag.GetAttributeValue("id") == id)
                    return tag;
            }
            return null;
        }

        public IEnumerable<HTMLTag> FindTags(bool isEndTag, HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            foreach (HTMLTag tag in EnumerateTags(startAfterTag, stopBeforeTag)) {
                if (tag.IsEnd == isEndTag && tag.NameEqualsAny(names)) {
                    yield return tag;
                }
            }
        }

        public IEnumerable<HTMLTag> FindTags(bool isEndTag, HTMLTagRange containingTagRange, params string[] names) {
            return FindTags(isEndTag, containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public HTMLTag FindTag(bool isEndTag, HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            foreach (HTMLTag tag in FindTags(isEndTag, startAfterTag, stopBeforeTag, names)) {
                return tag;
            }
            return null;
        }

        public HTMLTag FindTag(bool isEndTag, HTMLTagRange containingTagRange, params string[] names) {
            return FindTag(isEndTag, containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public IEnumerable<HTMLTag> FindStartTags(HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            return FindTags(false, startAfterTag, stopBeforeTag, names);
        }

        public IEnumerable<HTMLTag> FindStartTags(HTMLTagRange containingTagRange, params string[] names) {
            return FindStartTags(containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public IEnumerable<HTMLTag> FindStartTags(params string[] names) {
            return FindTags(false, null, null, names);
        }

        public HTMLTag FindStartTag(HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            return FindTag(false, startAfterTag, stopBeforeTag, names);
        }

        public HTMLTag FindStartTag(HTMLTagRange containingTagRange, params string[] names) {
            return FindStartTag(containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public HTMLTag FindStartTag(params string[] names) {
            return FindTag(false, null, null, names);
        }

        public IEnumerable<HTMLTag> FindEndTags(HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            return FindTags(true, startAfterTag, stopBeforeTag, names);
        }

        public IEnumerable<HTMLTag> FindEndTags(HTMLTagRange containingTagRange, params string[] names) {
            return FindEndTags(containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public IEnumerable<HTMLTag> FindEndTags(params string[] names) {
            return FindTags(true, null, null, names);
        }

        public HTMLTag FindEndTag(HTMLTag startAfterTag, HTMLTag stopBeforeTag, params string[] names) {
            return FindTag(true, startAfterTag, stopBeforeTag, names);
        }

        public HTMLTag FindEndTag(HTMLTagRange containingTagRange, params string[] names) {
            return FindEndTag(containingTagRange.StartTag, containingTagRange.EndTag, names);
        }

        public HTMLTag FindEndTag(params string[] names) {
            return FindTag(true, null, null, names);
        }

        public HTMLTag FindCorrespondingEndTag(HTMLTag tag) {
            return FindCorrespondingEndTag(tag, null);
        }

        public HTMLTag FindCorrespondingEndTag(HTMLTag tag, HTMLTag stopBeforeTag) {
            if (tag == null) {
                return null;
            }
            if (tag.IsEnd) {
                throw new ArgumentException("Tag must be a start tag.");
            }
            if (tag.IsSelfClosing) {
                return tag;
            }
            int startIndex = GetTagIndex(tag) + 1;
            int stopIndex = GetStopIndex(stopBeforeTag);
            return FindMatchingEndTag(tag.Name, startIndex, stopIndex);
        }

        private HTMLTag FindMatchingEndTag(string name, int startIndex, int stopIndex) {
            int depth = 1;
            for (int i = startIndex; i <= stopIndex; i++) {
                HTMLTag tag2 = _tags[i];
                if (!IsNestableTag(tag2, name)) continue;
                depth += tag2.IsEnd ? -1 : 1;
                if (depth == 0) {
                    return tag2;
                }
            }
            return null;
        }

        private static bool IsNestableTag(HTMLTag tag, string name) {
            return !tag.IsSelfClosing && tag.NameEquals(name);
        }

        public HTMLTagRange CreateTagRange(HTMLTag tag) {
            return CreateTagRange(tag, null);
        }

        public HTMLTagRange CreateTagRange(HTMLTag tag, HTMLTag stopBeforeTag) {
            HTMLTag endTag = FindCorrespondingEndTag(tag, stopBeforeTag);
            return (tag != null && endTag != null) ? new HTMLTagRange(tag, endTag) : null;
        }

        private int GetTagIndex(HTMLTag tag) {
            int i;
            if (!_offsetToIndex.TryGetValue(tag.Offset, out i)) {
                throw new Exception("Unable to locate the specified tag.");
            }
            return i;
        }

        private int GetStopIndex(HTMLTag stopBeforeTag) {
            return stopBeforeTag != null ? (GetTagIndex(stopBeforeTag) - 1) : (_tags.Count - 1);
        }

        private static string Preprocess(string html) {
            if (html.IndexOf('\r') == -1) {
                // No preprocessing needed
                return html;
            }
            char[] dst = new char[html.Length];
            int iDst = 0;
            for (int iSrc = 0; iSrc < html.Length; iSrc++) {
                char c = html[iSrc];
                if (IsLineFeedAfterCarriageReturn(html, iSrc)) {
                    // Skip line feed following carriage return
                    continue;
                }
                if (c == '\r') {
                    // Convert carriage return to line feed
                    c = '\n';
                }
                dst[iDst++] = c;
            }
            return new string(dst, 0, iDst);
        }

        private static bool IsLineFeedAfterCarriageReturn(string html, int index) {
            return html[index] == '\n' && index >= 1 && html[index - 1] == '\r';
        }

        // Parsing helpers below return the position to continue from, or -1 (or null)
        // when the input ends before the construct is complete, which stops parsing.
        private static IEnumerable<HTMLTag> ParseTags(string html, int htmlStart, int htmlEnd) {
            ForeignContent foreignContent = new ForeignContent();
            int pos;
            while ((pos = IndexOf(html, htmlStart, htmlEnd, '<')) != -1) {
                htmlStart = pos + 1;
                if (StartsWithTagName(html, htmlStart, htmlEnd)) {
                    HTMLTag tag = ParseTag(html, pos, htmlEnd);
                    if (tag == null) yield break;

                    // Yield result
                    yield return tag;

                    // Skip contents of special tags whose contents are to be treated as raw text
                    htmlStart = SkipRawTextContents(html, tag, foreignContent, htmlEnd);
                }
                else {
                    htmlStart = SkipNonTagMarkup(html, htmlStart, htmlEnd);
                }
                if (htmlStart == -1) yield break;
            }
        }

        private static bool StartsWithTagName(string html, int htmlStart, int htmlEnd) {
            if (StartsWith(html, htmlStart, htmlEnd, '/')) htmlStart += 1;
            return StartsWithLetter(html, htmlStart, htmlEnd);
        }

        private static HTMLTag ParseTag(string html, int tagOffset, int htmlEnd) {
            HTMLTag tag = new HTMLTag();
            tag.Offset = tagOffset;
            int htmlStart = tagOffset + 1;
            tag.IsEnd = StartsWith(html, htmlStart, htmlEnd, '/');

            // Parse tag name
            if (tag.IsEnd) htmlStart += 1;
            int pos = IndexOfAny(html, htmlStart, htmlEnd, true, '/', '>');
            if (pos == -1) return null;
            tag.Name = GetSectionLower(html, htmlStart, pos);

            // Parse attributes
            htmlStart = ParseAttributes(html, tag, pos, htmlEnd);
            if (htmlStart == -1) return null;
            tag.Length = htmlStart - tag.Offset;
            return tag;
        }

        private static int ParseAttributes(string html, HTMLTag tag, int htmlStart, int htmlEnd) {
            htmlStart = SkipToNextAttribute(html, tag, htmlStart, htmlEnd);
            while (!StartsWith(html, htmlStart, htmlEnd, '>')) {
                if (!tag.IsSelfClosing) {
                    htmlStart = ParseAttribute(html, tag, htmlStart, htmlEnd);
                    if (htmlStart == -1) return -1;
                }
                htmlStart = SkipToNextAttribute(html, tag, htmlStart, htmlEnd);
            }
            return htmlStart + 1;
        }

        private static int SkipToNextAttribute(string html, HTMLTag tag, int htmlStart, int htmlEnd) {
            htmlStart = SkipWhiteSpace(html, htmlStart, htmlEnd);
            tag.IsSelfClosing = StartsWith(html, htmlStart, htmlEnd, '/');
            if (tag.IsSelfClosing) htmlStart += 1;
            return htmlStart;
        }

        private static int ParseAttribute(string html, HTMLTag tag, int htmlStart, int htmlEnd) {
            HTMLAttribute attribute = new HTMLAttribute();
            attribute.Offset = htmlStart;

            // Parse attribute name
            int pos = IndexOfAny(html, htmlStart + 1, htmlEnd, true, '=', '/', '>');
            if (pos == -1) return -1;
            attribute.Name = GetSectionLower(html, htmlStart, pos);
            htmlStart = SkipWhiteSpace(html, pos, htmlEnd);

            if (StartsWith(html, htmlStart, htmlEnd, '=')) {
                htmlStart = ParseAttributeValue(html, attribute, htmlStart + 1, htmlEnd);
                if (htmlStart == -1) return -1;
            }
            else {
                attribute.Value = String.Empty;
            }

            attribute.Length = htmlStart - attribute.Offset;
            if (tag.GetAttribute(attribute.Name) == null) {
                tag.Attributes.Add(attribute);
            }
            else {
                tag.DuplicateAttributes.Add(attribute);
            }
            return htmlStart;
        }

        private static int ParseAttributeValue(string html, HTMLAttribute attribute, int htmlStart, int htmlEnd) {
            htmlStart = SkipWhiteSpace(html, htmlStart, htmlEnd);
            if (StartsWithAny(html, htmlStart, htmlEnd, '"', '\'')) {
                char quoteChar = html[htmlStart];
                htmlStart += 1;
                int quoteEnd = IndexOf(html, htmlStart, htmlEnd, quoteChar);
                if (quoteEnd == -1) return -1;
                attribute.Value = GetSection(html, htmlStart, quoteEnd);
                return quoteEnd + 1;
            }
            int valueEnd = IndexOfAny(html, htmlStart, htmlEnd, true, '>');
            if (valueEnd == -1) return -1;
            attribute.Value = GetSection(html, htmlStart, valueEnd);
            return valueEnd;
        }

        private static int SkipRawTextContents(string html, HTMLTag tag, ForeignContent foreignContent, int htmlEnd) {
            int htmlStart = tag.EndOffset;
            bool isRawText = IsRawTextStartTag(tag, foreignContent);
            foreignContent.Update(tag);
            if (!isRawText) return htmlStart;
            string endTagText = "/" + tag.Name;
            int pos;
            while ((pos = IndexOf(html, htmlStart, htmlEnd, '<')) != -1) {
                htmlStart = pos + 1;
                if (StartsWithRawTextEndTag(html, htmlStart, htmlEnd, endTagText)) return pos;
            }
            return -1;
        }

        // In svg and math a browser reads the contents of title, style and textarea as markup.
        // Script contents stay text here: the removal of scripts takes their contents with them.
        private static bool IsRawTextStartTag(HTMLTag tag, ForeignContent foreignContent) {
            if (tag.IsEnd || tag.IsSelfClosing) return false;
            return tag.NameEquals("script") || (tag.NameEqualsAny("style", "title", "textarea") && foreignContent.IsReadAsHTML(tag));
        }

        private static bool StartsWithRawTextEndTag(string html, int htmlStart, int htmlEnd, string endTagText) {
            return StartsWith(html, htmlStart, htmlEnd, endTagText, true) &&
                (StartsWithWhiteSpace(html, htmlStart + endTagText.Length, htmlEnd) ||
                 StartsWithAny(html, htmlStart + endTagText.Length, htmlEnd, '/', '>'));
        }

        private static int SkipNonTagMarkup(string html, int htmlStart, int htmlEnd) {
            if (StartsWithCommentStart(html, htmlStart, htmlEnd)) {
                // Skip comment
                return SkipComment(html, htmlStart + 3, htmlEnd);
            }
            if (StartsWithAny(html, htmlStart, htmlEnd, '?', '/', '!')) {
                // Skip bogus comment or DOCTYPE
                int pos = IndexOf(html, htmlStart + 1, htmlEnd, '>');
                if (pos == -1) return -1;
                return pos + 1;
            }
            return htmlStart;
        }

        // Browsers end "<!-->" and "<!--->" at once, as the bogus comment rule ends them
        private static bool StartsWithCommentStart(string html, int htmlStart, int htmlEnd) {
            return StartsWith(html, htmlStart, htmlEnd, "!--", false) && !StartsWith(html, htmlStart + 3, htmlEnd, '>') &&
                !StartsWith(html, htmlStart + 3, htmlEnd, "->", false);
        }

        private static int SkipComment(string html, int htmlStart, int htmlEnd) {
            int pos;
            while ((pos = IndexOf(html, htmlStart, htmlEnd, '-')) != -1) {
                htmlStart = pos + 1;
                if (StartsWith(html, htmlStart, htmlEnd, "->", false)) return htmlStart + 2;
                if (StartsWith(html, htmlStart, htmlEnd, "-!>", false)) return htmlStart + 3;
            }
            return -1;
        }

        private static int SkipWhiteSpace(string html, int htmlStart, int htmlEnd) {
            while (StartsWithWhiteSpace(html, htmlStart, htmlEnd)) htmlStart++;
            return htmlStart;
        }

        private static int IndexOf(string html, int htmlStart, int htmlEnd, char value) {
            while (htmlStart < htmlEnd) {
                if (html[htmlStart] == value) {
                    return htmlStart;
                }
                htmlStart++;
            }
            return -1;
        }

        private static int IndexOfAny(string html, int htmlStart, int htmlEnd, bool findWhiteSpace, params char[] values) {
            while (htmlStart < htmlEnd) {
                char c = html[htmlStart];
                if (findWhiteSpace && CharIsWhiteSpace(c)) {
                    return htmlStart;
                }
                foreach (char v in values) {
                    if (c == v) {
                        return htmlStart;
                    }
                }
                htmlStart++;
            }
            return -1;
        }

        private static bool StartsWith(string html, int htmlStart, int htmlEnd, char value) {
            if (htmlStart >= htmlEnd) return false;
            return html[htmlStart] == value;
        }

        private static bool StartsWith(string html, int htmlStart, int htmlEnd, string value, bool ignoreCase) {
            if (htmlStart + (value.Length - 1) >= htmlEnd) return false;
            for (int i = 0; i < value.Length; i++) {
                char c = html[htmlStart + i];
                char v = value[i];
                if (ignoreCase) {
                    c = CharToLower(c);
                    v = CharToLower(v);
                }
                if (c != v) return false;
            }
            return true;
        }

        private static bool StartsWithAny(string html, int htmlStart, int htmlEnd, params char[] values) {
            if (htmlStart >= htmlEnd) return false;
            char c = html[htmlStart];
            foreach (char v in values) {
                if (c == v) return true;
            }
            return false;
        }

        private static bool StartsWithWhiteSpace(string html, int htmlStart, int htmlEnd) {
            if (htmlStart >= htmlEnd) return false;
            char c = html[htmlStart];
            return CharIsWhiteSpace(c);
        }

        private static bool StartsWithLetter(string html, int htmlStart, int htmlEnd) {
            if (htmlStart >= htmlEnd) return false;
            return CharIsLetter(html[htmlStart]);
        }

        private static string GetSectionLower(string html, int htmlStart, int htmlEnd) {
            char[] dst = new char[htmlEnd - htmlStart];
            for (int i = 0; i < dst.Length; i++) {
                dst[i] = CharToLower(html[htmlStart + i]);
            }
            return new string(dst);
        }

        private static string GetSection(string html, int htmlStart, int htmlEnd) {
            return html.Substring(htmlStart, htmlEnd - htmlStart);
        }

        private static bool CharIsLetter(char c) {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool CharIsWhiteSpace(char c) {
            return c == ' ' || c == '\t' || c == '\f' || c == '\n';
        }

        private static char CharToLower(char c) {
            return (c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c;
        }

        public static char[] GetWhiteSpaceChars() {
            return new char[] { ' ', '\t', '\f', '\n' };
        }

        public static bool ClassAttributeValueHas(string attributeValue, string targetClassName) {
            string[] assignedClassNames = attributeValue.Split(GetWhiteSpaceChars(), StringSplitOptions.RemoveEmptyEntries);
            return Array.Exists(assignedClassNames, n => n.Equals(targetClassName, StringComparison.Ordinal));
        }

        public static bool ClassAttributeValueHas(HTMLTag tag, string targetClassName) {
            string attributeValue = tag.GetAttributeValue("class");
            return attributeValue != null && ClassAttributeValueHas(attributeValue, targetClassName);
        }

        // The svg and math elements open at a point of the page, as far as a browser's tree
        // builder uses them to decide if it reads a start tag as HTML or as foreign content.
        // Inside an integration point (svg foreignObject, desc and title, MathML mi, mo, mn, ms,
        // mtext, and annotation-xml for HTML) a browser reads start tags as HTML again. HTML
        // elements are not followed, so an end tag inside an integration point is matched against
        // the foreign elements even where a browser ignores it for an HTML element still open.
        private sealed class ForeignContent {
            private static readonly string[] _breakoutNames = {
                "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed",
                "h1", "h2", "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta",
                "nobr", "ol", "p", "pre", "ruby", "s", "small", "span", "strong", "strike", "sub", "sup", "table",
                "tt", "u", "ul", "var"
            };

            private readonly List<ForeignElement> _openElements = new List<ForeignElement>();

            public bool IsReadAsHTML(HTMLTag startTag) {
                if (_openElements.Count == 0) return true;
                ForeignElement current = _openElements[_openElements.Count - 1];
                return current.IsHTMLIntegrationPoint || (current.IsTextIntegrationPoint && !startTag.NameEqualsAny("mglyph", "malignmark"));
            }

            public void Update(HTMLTag tag) {
                if (tag.IsEnd) {
                    Close(tag);
                }
                else if (IsReadAsHTML(tag)) {
                    OpenRoot(tag);
                }
                else {
                    OpenChild(tag);
                }
            }

            private void OpenRoot(HTMLTag tag) {
                if (!tag.IsSelfClosing && tag.NameEqualsAny("svg", "math")) {
                    _openElements.Add(new ForeignElement(tag, tag.NameEquals("svg")));
                }
            }

            // An HTML element such as a div or p ends the foreign content it appears in
            private void OpenChild(HTMLTag tag) {
                if (IsBreakoutTag(tag)) {
                    CloseToHTML();
                }
                else if (!tag.IsSelfClosing) {
                    _openElements.Add(new ForeignElement(tag, IsSvgChild(tag)));
                }
            }

            private bool IsSvgChild(HTMLTag tag) {
                ForeignElement current = _openElements[_openElements.Count - 1];
                return current.IsSvg || (tag.NameEquals("svg") && current.Name == "annotation-xml");
            }

            private static bool IsBreakoutTag(HTMLTag tag) {
                return tag.NameEqualsAny(_breakoutNames) || (tag.NameEquals("font") && IsBreakoutFont(tag));
            }

            private static bool IsBreakoutFont(HTMLTag tag) {
                return tag.GetAttribute("color") != null || tag.GetAttribute("face") != null || tag.GetAttribute("size") != null;
            }

            private void Close(HTMLTag tag) {
                if (tag.NameEqualsAny("br", "p")) {
                    CloseToHTML();
                    return;
                }
                int i = _openElements.FindLastIndex(e => e.Name == tag.Name);
                if (i != -1) _openElements.RemoveRange(i, _openElements.Count - i);
            }

            private void CloseToHTML() {
                int i = _openElements.FindLastIndex(e => e.IsHTMLIntegrationPoint || e.IsTextIntegrationPoint);
                _openElements.RemoveRange(i + 1, _openElements.Count - (i + 1));
            }
        }

        private sealed class ForeignElement {
            public ForeignElement(HTMLTag tag, bool isSvg) {
                Name = tag.Name;
                IsSvg = isSvg;
                IsHTMLIntegrationPoint = isSvg ? tag.NameEqualsAny("foreignobject", "desc", "title") : IsHTMLAnnotation(tag);
                IsTextIntegrationPoint = !isSvg && tag.NameEqualsAny("mi", "mo", "mn", "ms", "mtext");
            }

            public string Name { get; private set; }
            public bool IsSvg { get; private set; }
            public bool IsHTMLIntegrationPoint { get; private set; }
            public bool IsTextIntegrationPoint { get; private set; }

            private static bool IsHTMLAnnotation(HTMLTag tag) {
                string encoding = tag.GetAttributeValueOrEmpty("encoding");
                return tag.NameEquals("annotation-xml") &&
                    (encoding.Equals("text/html", StringComparison.OrdinalIgnoreCase) || encoding.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public class HTMLTag {
        public string Name { get; set; }
        public bool IsEnd { get; set; }
        public bool IsSelfClosing { get; set; }
        public List<HTMLAttribute> Attributes { get; set; }
        // Later attributes with a name already in Attributes; browsers ignore them
        public List<HTMLAttribute> DuplicateAttributes { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }

        public HTMLTag() {
            Attributes = new List<HTMLAttribute>();
            DuplicateAttributes = new List<HTMLAttribute>();
        }

        public int EndOffset {
            get { return Offset + Length; }
        }

        public bool NameEquals(string name) {
            return Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        public bool NameEqualsAny(params string[] names) {
            foreach (string name in names) {
                if (Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        public HTMLAttribute GetAttribute(string name) {
            foreach (HTMLAttribute attribute in Attributes) {
                if (attribute.NameEquals(name)) {
                    return attribute;
                }
            }
            return null;
        }

        public string GetAttributeValue(string attributeName) {
            HTMLAttribute attribute = GetAttribute(attributeName);
            return attribute != null ? attribute.Value : null;
        }

        public string GetAttributeValueOrEmpty(string attributeName) {
            return GetAttributeValue(attributeName) ?? String.Empty;
        }
    }

    public class HTMLAttribute {
        public string Name { get; set; }
        public string Value { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }

        public bool NameEquals(string name) {
            return Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class HTMLTagRange {
        public HTMLTag StartTag { get; set; }
        public HTMLTag EndTag { get; set; }

        public HTMLTagRange(HTMLTag startTag, HTMLTag endTag) {
            StartTag = startTag;
            EndTag = endTag;
        }

        public int Offset {
            get { return StartTag.Offset; }
        }

        public int EndOffset {
            get { return EndTag.EndOffset; }
        }

        public int Length {
            get { return EndOffset - Offset; }
        }
    }
}