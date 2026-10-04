#!/usr/bin/env python3
"""Turns a captured imageboard thread page into a sterile test fixture.

The output is rebuilt from the parsed page and keeps only the markup the site helpers read
(ChanThreadWatch.Tests/Fixtures/sites/allowlist.json). Every value is replaced with a generated
placeholder: post numbers, names, tripcodes, poster IDs, file names, MD5s, hosts and URL words.
Post text, subjects, dates and everything else are dropped.

After sanitizing, the real site helper is run on the raw capture and on the fixture (through
Get-SiteHelperResult.ps1, using the built ChanThreadWatch.Core.dll). The fixture is only written when
both runs give corresponding results: the same number of images, thumbnails and cross links, and
values that map one to one.

Raw captures contain real user data. Keep them outside the repository and delete them after use.

Usage:
  python sanitize_site_fixture.py --raw <capture.html> --url <thread URL> --helper FourChanSiteHelper
      --name 4chan --capture direct [--dry-run]
"""

import argparse
import base64
import datetime
import hashlib
import html
import json
import os
import re
import subprocess
import sys
import tempfile
from html.parser import HTMLParser
from urllib.parse import unquote, urlsplit

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SITES_DIR = os.path.join(REPO, "ChanThreadWatch.Tests", "Fixtures", "sites")
ALLOWLIST_PATH = os.path.join(SITES_DIR, "allowlist.json")
MANIFEST_PATH = os.path.join(SITES_DIR, "manifest.json")
CORE_DLL_PATH = os.path.join(REPO, "bin", "Release", "ChanThreadWatch.Core.dll")
RESULT_SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Get-SiteHelperResult.ps1")

# Values the placeholders stand for when a fixture is parsed. The tests use the same values.
BASE_URL = "http://fixture.test"
MEDIA_URL = "http://media.test"

VOID_TAGS = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}
BLOCK_TAGS = {"html", "head", "body", "div", "article", "figure", "header", "p", "blockquote", "label", "table", "tbody", "tr", "td"}
ALWAYS_KEPT_TAGS = {"html", "head", "body", "input", "label", "img", "table", "tbody", "tr", "td"}


# The base64 MD5 of "fixture-image-N-K", with K the smallest number from 0 whose base64 MD5
# contains both "/" and "+", so the standard form ({{md5s_N}}) always has the characters a
# URL-safe MD5 replaces. SiteFixtures.PlaceholderMD5 in the tests uses the same values.
def placeholder_md5(index):
    k = 0
    while True:
        value = base64.b64encode(hashlib.md5(b"fixture-image-%d-%d" % (index, k)).digest()).decode("ascii")
        if "/" in value and "+" in value:
            return value
        k += 1


class Node:
    def __init__(self, tag, attrs, parent):
        self.tag = tag
        self.attrs = attrs
        self.parent = parent
        self.children = []

    def classes(self):
        return (self.attrs.get("class") or "").split()

    def text(self):
        parts = []
        for child in self.children:
            if isinstance(child, Node):
                parts.append(child.text())
            elif isinstance(child, str):
                parts.append(child)
        return "".join(parts)

    def comments(self):
        return [c.data for c in self.children if isinstance(c, Comment)]


class Comment:
    def __init__(self, data):
        self.data = data


class TreeBuilder(HTMLParser):
    """Builds a tree with a stack. An end tag closes the nearest open element of that name;
    a stray end tag is ignored."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node("#document", {}, None)
        self.stack = [self.root]

    def add_node(self, tag, attrs):
        # Like HTMLParser in the app, the first of repeated attributes wins
        node = Node(tag, {}, self.stack[-1])
        for name, value in attrs:
            node.attrs.setdefault(name, value if value is not None else "")
        self.stack[-1].children.append(node)
        return node

    def handle_starttag(self, tag, attrs):
        node = self.add_node(tag, attrs)
        if tag not in VOID_TAGS:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.add_node(tag, attrs)

    def handle_endtag(self, tag):
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                return

    def handle_data(self, data):
        self.stack[-1].children.append(data)

    def handle_comment(self, data):
        self.stack[-1].children.append(Comment(data))


class Mapper:
    """Replaces real values with generated placeholders. The same real value always gets the
    same placeholder, so links and ids that matched in the capture still match."""

    def __init__(self, allowlist):
        self.keywords = set(allowlist["urlKeywords"])
        self.extensions = set(allowlist["fileExtensions"])
        self.maps = {}

    def _index(self, kind, key):
        table = self.maps.setdefault(kind, {})
        if key not in table:
            table[key] = len(table) + 1
        return table[key]

    def number(self, digits):
        return "777%07d" % self._index("number", digits.lstrip("0") or "0")

    def word(self, word):
        return "w%d" % self._index("word", word.lower())

    def hex(self, value):
        index = self._index("hex", value.lower())
        return "f" * max(len(value) - 6, 1) + "%06d" % index

    def md5_index(self, value):
        raw = decode_md5(value)
        return None if raw is None else self._index("md5", raw)

    def file_name(self, name):
        name = name.strip()
        ext = name.rsplit(".", 1)[1].lower() if "." in name else ""
        ext = ext if ext in self.extensions else "bin"
        return "file-%d.%s" % (self._index("file", name), ext)

    def name(self, text):
        text = text.strip()
        if text == "Anonymous" or text == "":
            return text
        return "name-%d" % self._index("name", text)

    def trip(self, text):
        text = text.strip()
        return "!trip-%d" % self._index("trip", text) if text else ""

    def poster_id(self, text):
        text = text.strip()
        if not text:
            return ""
        prefix = "ID:" if text.startswith("ID:") else ""
        return "%sid-%d" % (prefix, self._index("posterId", text))

    def dead_link(self, text):
        text = text.strip()
        match = re.fullmatch(r">>>/([^/]+)/(\d+)", text)
        if match:
            return ">>>/%s/%s" % (self.word(match.group(1)), self.number(match.group(2)))
        match = re.fullmatch(r">>(\d+)", text)
        return ">>" + self.number(match.group(1)) if match else ""

    def url_tokens(self, text):
        out = []
        for token in re.findall(r"[A-Za-z0-9]+|[^A-Za-z0-9]+", text):
            if not token[0].isalnum():
                out.append("".join(c for c in token if c in "-_."))
            elif len(token) >= 16 and re.fullmatch(r"[0-9a-fA-F]+", token) and re.search(r"\d", token):
                out.append(self.hex(token))
            else:
                out.append(self.alnum(token))
        return "".join(out)

    def alnum(self, token):
        out = []
        for part in re.findall(r"[0-9]+|[A-Za-z]+", token):
            if part.isdigit():
                out.append(self.number(part))
            elif part.lower() in self.keywords:
                out.append(part.lower())
            else:
                out.append(self.word(part))
        return "".join(out)

    def segment(self, segment, previous):
        if segment == "":
            return ""
        if previous == "image" and re.fullmatch(r"[A-Za-z0-9_-]{22}", segment):
            index = self.md5_index(segment)
            if index is not None:
                return "{{md5u_%d}}" % index
        base, dot, ext = segment.rpartition(".")
        if dot and base and ext.lower() in self.extensions:
            return self.url_tokens(base) + "." + ext.lower()
        return self.url_tokens(segment)

    def image_md5_path(self, segments):
        """Maps a "same image" path (/<board>/image/<md5>, optionally with a trailing "/") whose MD5
        is standard base64, and so can contain "/" or "+" and span segments, or is percent-encoded.
        A standard MD5 becomes {{md5s_N}} and a percent-encoded one {{md5u_N}}. Returns None if
        the path has no such MD5; a URL-safe MD5 is one segment, which segment() maps."""
        lowered = [s.lower() for s in segments]
        if "image" not in lowered:
            return None
        start = lowered.index("image") + 1
        end = len(segments) - 1 if len(segments) > start + 1 and segments[-1] == "" else len(segments)
        md5 = "/".join(segments[start:end])
        index = self.md5_index(unquote(md5)) if re.search(r"[/+%]", md5) else None
        if index is None:
            return None
        head = [self.segment(s, lowered[i - 1] if i > 0 else "") for i, s in enumerate(segments[:start])]
        form = "md5s" if re.search(r"[/+]", md5) else "md5u"
        return "/".join(head + ["{{%s_%d}}" % (form, index)] + segments[end:])

    def url(self, value, page_host):
        value = value.strip()
        parts = urlsplit(value)
        if parts.scheme and parts.scheme not in ("http", "https"):
            return None
        prefix = ""
        if parts.netloc:
            prefix = "{{base}}" if (parts.hostname or "") == page_host else "{{media}}"
        segments = parts.path.split("/")
        path = self.image_md5_path(segments)
        if path is None:
            mapped = [self.segment(s, segments[i - 1].lower() if i > 0 else "") for i, s in enumerate(segments)]
            path = "/".join(mapped)
        if prefix and not path.startswith("/"):
            path = "/" + path
        fragment = "#" + self.url_tokens(parts.fragment) if parts.fragment else ""
        result = prefix + path + fragment
        return result if result else None


# A bare link is kept only if it points at a post, a thread or a file (by number, MD5, hash
# name or media extension); navigation links to boards and pages are dropped.
def is_post_link(href):
    return re.search(r"777[0-9]{7}|\{\{md5[us]_|f[0-9]{6}|\.(jpe?g|png|gif|webp|webm|mp4)$", href) is not None


def decode_md5(value):
    text = value.strip().replace("-", "+").replace("_", "/")
    text += "=" * (-len(text) % 4)
    try:
        raw = base64.b64decode(text, validate=True)
    except ValueError:
        return None
    return raw if len(raw) == 16 else None


class Sanitizer:
    def __init__(self, allowlist, page_url):
        self.allowlist = allowlist
        self.kept_classes = set(allowlist["keptClasses"])
        self.kept_tags = set(allowlist["keptTags"])
        self.dropped_tags = set(allowlist["droppedTags"])
        self.id_prefixes = set(allowlist["idPrefixes"])
        self.page_host = urlsplit(page_url).hostname
        self.mapper = Mapper(allowlist)
        self.text_contexts = [(kind, selector) for kind, selectors in allowlist["textContexts"].items() for selector in selectors]

    def text_context(self, node):
        classes = node.classes()
        for kind, selector in self.text_contexts:
            tag, _, cls = selector.rpartition(".")
            if cls in classes and (not tag or tag == node.tag):
                return kind
        # 4chan: the link inside the fileText element holds the original file name
        if node.tag == "a" and node.parent is not None and "fileText" in node.parent.classes():
            return "fileName"
        return None

    def holds_file_name_title(self, node, context):
        return context == "fileName" or (node.tag == "div" and "fileText" in node.classes())

    def clean_attributes(self, node, context):
        attrs = []
        classes = [c for c in node.classes() if c in self.kept_classes]
        if classes:
            attrs.append(("class", " ".join(classes)))
        if "id" in node.attrs:
            match = re.fullmatch(r"([A-Za-z_]*?)(\d+)", node.attrs["id"])
            if match and match.group(1) in self.id_prefixes:
                attrs.append(("id", match.group(1) + self.mapper.number(match.group(2))))
        for name in ("href", "src"):
            if name in node.attrs:
                url = self.mapper.url(node.attrs[name], self.page_host)
                if url is not None:
                    attrs.append((name, url))
        if "data-md5" in node.attrs:
            index = self.mapper.md5_index(node.attrs["data-md5"])
            if index is not None:
                attrs.append(("data-md5", "{{md5_%d}}" % index))
        if "title" in node.attrs and self.holds_file_name_title(node, context):
            attrs.append(("title", self.mapper.file_name(node.attrs["title"])))
        if node.attrs.get("target") == "_blank":
            attrs.append(("target", "_blank"))
        return attrs

    def context_text(self, node, context):
        text = html.unescape(node.text())
        if context == "name":
            return [self.mapper.name(text)]
        if context == "trip":
            return [self.mapper.trip(text)]
        if context == "posterId":
            return [self.mapper.poster_id(text)]
        if context == "fileName":
            return [self.mapper.file_name(text)] if text.strip() else []
        if context == "deadLink":
            return [self.mapper.dead_link(text)]
        return self.fuuka_file_info(node, text)

    # Fuuka's file info reads "File: <size>, <dimensions>, <file name>", optionally followed by
    # the MD5 in an HTML comment. Size and dimensions become fixed values.
    def fuuka_file_info(self, node, text):
        split = text.split(",", 2)
        if len(split) < 3:
            return []
        out = ["File: 10 KB, 64x64, " + self.mapper.file_name(split[2])]
        for comment in node.comments():
            index = self.mapper.md5_index(comment)
            if index is not None:
                out.append(Comment(" {{md5_%d}} " % index))
        return out

    def clean(self, node):
        if node.tag in self.dropped_tags:
            return []
        context = self.text_context(node)
        if context is not None:
            children = self.context_text(node, context)
        else:
            children = [c for child in node.children if isinstance(child, Node) for c in self.clean(child)]
        if node.tag not in self.kept_tags:
            return children
        attrs = self.clean_attributes(node, context)
        if self.is_kept(node, attrs, children, context):
            out = Node(node.tag, dict(attrs), None)
            out.attr_list = attrs
            out.children = children
            return [out]
        return children

    def is_kept(self, node, attrs, children, context):
        values = dict(attrs)
        return (node.tag in ALWAYS_KEPT_TAGS or context is not None or bool(children) or
                "class" in values or "id" in values or (node.tag == "a" and is_post_link(values.get("href", ""))))

    def run(self, raw_html):
        builder = TreeBuilder()
        builder.feed(raw_html)
        builder.close()
        html_nodes = [c for c in builder.root.children if isinstance(c, Node)]
        out = []
        for node in html_nodes:
            out.extend(self.clean(node))
        return "<!DOCTYPE html>\n" + "".join(serialize(n) for n in out) + "\n"


def serialize(node):
    if isinstance(node, Comment):
        return "<!--%s-->" % node.data
    if isinstance(node, str):
        return html.escape(node, quote=False)
    attrs = "".join(' %s="%s"' % (name, html.escape(value, quote=True)) for name, value in node.attr_list)
    start = ("\n" if node.tag in BLOCK_TAGS else "") + "<%s%s>" % (node.tag, attrs)
    if node.tag in VOID_TAGS:
        return start
    return start + "".join(serialize(c) for c in node.children) + "</%s>" % node.tag


def substitute(fixture_html):
    """Replaces the placeholders with the values the tests use."""
    text = fixture_html.replace("{{base}}", BASE_URL).replace("{{media}}", MEDIA_URL)
    text = re.sub(r"\{\{md5_(\d+)\}\}", lambda m: placeholder_md5(int(m.group(1))), text)
    text = re.sub(r"\{\{md5s_(\d+)\}\}", lambda m: placeholder_md5(int(m.group(1))).rstrip("="), text)
    return re.sub(r"\{\{md5u_(\d+)\}\}", lambda m: placeholder_md5(int(m.group(1))).rstrip("=").replace("+", "-").replace("/", "_"), text)


def helper_result(helper, url, html_path):
    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", RESULT_SCRIPT,
               "-CoreDll", CORE_DLL_PATH, "-Helper", helper, "-Url", url, "-Html", html_path]
    output = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    if output.returncode != 0:
        raise SystemExit("Get-SiteHelperResult.ps1 failed: " + output.stderr.strip())
    return json.loads(output.stdout)


def corresponds(name, raw_values, fixture_values, problems):
    """True if the two lists have the same length and map one to one, element by element."""
    if len(raw_values) != len(fixture_values):
        problems.append("%s: %d in the capture, %d in the fixture" % (name, len(raw_values), len(fixture_values)))
        return
    forward, backward = {}, {}
    for raw, fixture in zip(raw_values, fixture_values):
        if (raw in (None, "")) != (fixture in (None, "")) or forward.setdefault(raw, fixture) != fixture or backward.setdefault(fixture, raw) != raw:
            problems.append("%s: values do not map one to one" % name)
            return


def compare(raw, fixture):
    problems = []
    if raw["isThread"] != fixture["isThread"]:
        problems.append("isThread differs")
    for field in ("url", "originalFileName", "poster", "hash", "hashType"):
        corresponds("images." + field, [i[field] for i in raw["images"]], [i[field] for i in fixture["images"]], problems)
    corresponds("thumbnails", raw["thumbnails"], fixture["thumbnails"], problems)
    corresponds("crossLinks", raw["crossLinks"], fixture["crossLinks"], problems)
    return problems


def update_manifest(name, entry):
    manifest = {}
    if os.path.exists(MANIFEST_PATH):
        with open(MANIFEST_PATH, encoding="utf-8") as f:
            manifest = json.load(f)
    manifest[name] = entry
    with open(MANIFEST_PATH, "w", encoding="utf-8", newline="\n") as f:
        json.dump(dict(sorted(manifest.items())), f, indent=2)
        f.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--raw", required=True, help="captured page (kept outside the repository)")
    parser.add_argument("--url", required=True, help="URL the page was captured from")
    parser.add_argument("--helper", required=True, help="site helper class, e.g. FourChanSiteHelper")
    parser.add_argument("--name", required=True, help="fixture name, e.g. 4chan")
    parser.add_argument("--capture", required=True, choices=["direct", "firecrawl"], help="how the page was captured")
    parser.add_argument("--captured", default=datetime.date.today().isoformat(), help="capture date, YYYY-MM-DD (default: today)")
    parser.add_argument("--dry-run", action="store_true", help="sanitize and verify, but write nothing to the repository")
    args = parser.parse_args()

    if not re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", args.name):
        raise SystemExit("--name must be lowercase letters, digits and dashes.")
    try:
        datetime.date.fromisoformat(args.captured)
    except ValueError:
        raise SystemExit("--captured must be a date, YYYY-MM-DD.")
    if os.path.normcase(os.path.realpath(args.raw)).startswith(os.path.normcase(os.path.realpath(REPO)) + os.sep):
        raise SystemExit("The raw capture must be outside the repository.")
    with open(ALLOWLIST_PATH, encoding="utf-8") as f:
        allowlist = json.load(f)
    with open(args.raw, encoding="utf-8", errors="replace") as f:
        raw_html = f.read()

    sanitizer = Sanitizer(allowlist, args.url)
    fixture_html = sanitizer.run(raw_html)
    page_path = sanitizer.mapper.url(urlsplit(args.url)._replace(scheme="", netloc="").geturl(), None)

    with tempfile.TemporaryDirectory() as temp:
        fixture_path = os.path.join(temp, "fixture.html")
        with open(fixture_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(substitute(fixture_html))
        raw_result = helper_result(args.helper, args.url, os.path.abspath(args.raw))
        fixture_result = helper_result(args.helper, BASE_URL + page_path, fixture_path)

    problems = compare(raw_result, fixture_result)
    print("%s: %d bytes, %d images, %d thumbnails, %d cross links, thread page: %s" % (
        args.name, len(fixture_html), len(fixture_result["images"]), len(fixture_result["thumbnails"]),
        len(fixture_result["crossLinks"]), fixture_result["isThread"]))
    if problems:
        print("The fixture does not match the capture:\n  " + "\n  ".join(problems))
        return 1
    if args.dry_run:
        print("Dry run: nothing written.")
        return 0

    with open(os.path.join(SITES_DIR, args.name + ".html"), "w", encoding="utf-8", newline="\n") as f:
        f.write(fixture_html)
    update_manifest(args.name, {
        "helper": args.helper,
        "pagePath": page_path,
        "capture": args.capture,
        "captured": args.captured,
        "images": len(fixture_result["images"]),
        "hashes": sum(1 for i in fixture_result["images"] if i["hash"]),
        "thumbnails": len(fixture_result["thumbnails"]),
        "crossLinks": len(fixture_result["crossLinks"]),
    })
    print("Wrote %s.html and its manifest entry." % args.name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
