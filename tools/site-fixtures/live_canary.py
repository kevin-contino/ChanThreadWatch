#!/usr/bin/env python3
"""Checks that each site helper still parses its imageboard's current markup.

For each site, fetches the board index, picks the first thread link, fetches that thread into a
temporary folder outside the repository and runs sanitize_site_fixture.py --dry-run on it. That
runs the real site helper (from src/ChanThreadWatch/bin/Release/ChanThreadWatch.Core.dll) on the live page and on its
sanitized form, and checks that the results correspond. A site fails if the fetch fails, the
verification fails, the page is not a thread page, or the helper finds no images.

The output goes to public CI logs, so it never contains page content, post text, names, thread
URLs or thread numbers: only the site name, pass or fail, image and thumbnail counts, and generic
failure reasons. The captures are deleted at the end.

Usage:
  python live_canary.py [--dry-run]
"""

import argparse
import gzip
import http.client
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zlib
from urllib.parse import urljoin, urlsplit

SANITIZER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sanitize_site_fixture.py")
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
FETCH_TIMEOUT = 30
RETRY_WAIT = 10
SANITIZER_TIMEOUT = 240

# One work-safe board per helper kind, matching the committed fixtures. The thread regex runs on
# the index page; its first group is the thread link (relative or absolute, without a fragment).
# The first match on the index's own host is used.
# Cloudflare-blocked hosts (desuarchive.org, archived.moe) are left out: the canary fetches directly.
SITES = [
    {"name": "4chan", "helper": "FourChanSiteHelper",
     "index": "https://boards.4chan.org/wg/", "thread": r'href="([^"#]*/wg/thread/\d+)'},
    {"name": "8ch", "helper": "InfinitechanSiteHelper",
     "index": "https://8ch.net/tech/", "thread": r'href="([^"#]*/tech/res/\d+\.html)'},
    {"name": "fuuka", "helper": "FuukaSiteHelper",
     "index": "https://warosu.org/ck/", "thread": r'href="([^"#]*/ck/thread/\d+)'},
    {"name": "foolfuuka", "helper": "FoolFuukaSiteHelper",
     "index": "https://archive.alice.al/c/", "thread": r'href="([^"#]*/c/thread/\d+/?)'},
    {"name": "lynxchan", "helper": "LynxChanSiteHelper",
     "index": "https://endchan.net/operate/", "thread": r'href="([^"#]*/operate/res/\d+\.html)'},
]

# Problem names the sanitizer prints on a mismatch. Only these are passed through.
# A problem line names the field, then either both counts or that the values do not map one to
# one. Only those two forms are kept, so the counts are the only values that reach the log.
PROBLEM = re.compile(r"^\s+(isThread|images\.(?:url|originalFileName|poster|hash|hashType)|thumbnails|crossLinks)\b"
                     r"(?:: (\d+) in the capture, (\d+) in the fixture$|(: values do not map one to one)$)?")
COUNTS = re.compile(r"^canary: \d+ bytes, (\d+) images, (\d+) thumbnails, \d+ cross links, thread page: (True|False)$")


class SiteFailure(Exception):
    """A failure whose message is generic and safe to print."""


def fetch_once(url):
    request = urllib.request.Request(url, headers={
        "User-Agent": USER_AGENT,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9",
    })
    with urllib.request.urlopen(request, timeout=FETCH_TIMEOUT) as response:
        body = response.read()
        encoding = (response.headers.get("Content-Encoding") or "").lower()
    if encoding == "gzip":
        return gzip.decompress(body)
    if encoding == "deflate":
        return zlib.decompress(body)
    return body


def fetch(url, what):
    """Fetches a URL, retrying once after a short wait. Failure messages never contain the URL."""
    for attempt in (1, 2):
        try:
            return fetch_once(url)
        except urllib.error.HTTPError as e:
            reason = "HTTP %d" % e.code
        except (urllib.error.URLError, http.client.HTTPException, OSError, zlib.error, EOFError) as e:
            reason = "network error (%s)" % type(e).__name__
        if attempt == 1:
            time.sleep(RETRY_WAIT)
    raise SiteFailure("fetch failed (%s): %s" % (what, reason))


def find_thread(site, index_html):
    """Returns the first thread link on the index's own host. Archives also link to the original
    board (warosu links each thread to 4chan), and those pages need a different helper."""
    host = urlsplit(site["index"]).hostname
    for match in re.finditer(site["thread"], index_html):
        url = urljoin(site["index"], match.group(1))
        if urlsplit(url).hostname == host:
            return url
    raise SiteFailure("no thread link found")


def describe_problem(match):
    """Returns e.g. "images.url (3 in capture, 2 in fixture)" or "images.url (not one to one)"."""
    if match.group(2):
        return "%s (%s in capture, %s in fixture)" % (match.group(1), match.group(2), match.group(3))
    if match.group(4):
        return "%s (not one to one)" % match.group(1)
    return match.group(1)


def verify(site, thread_url, capture_path):
    """Runs the sanitizer in verify mode and returns (images, thumbnails). Its stderr is never
    printed, because an error from PowerShell can quote the URL or the page."""
    command = [sys.executable, SANITIZER, "--raw", capture_path, "--url", thread_url, "--helper", site["helper"],
               "--name", "canary", "--capture", "direct", "--dry-run"]
    try:
        output = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                timeout=SANITIZER_TIMEOUT)
    except subprocess.TimeoutExpired:
        raise SiteFailure("verification failed: sanitizer timed out")
    lines = output.stdout.splitlines()
    counts = next((m for m in map(COUNTS.match, lines) if m), None)
    if output.returncode != 0:
        problems = [describe_problem(m) for m in map(PROBLEM.match, lines) if m]
        if counts is None or not problems:
            raise SiteFailure("verification failed: sanitizer error (exit code %d)" % output.returncode)
        raise SiteFailure("verification failed: " + ", ".join(problems))
    if counts is None:
        raise SiteFailure("verification failed: unexpected sanitizer output")
    images, thumbnails, is_thread = int(counts.group(1)), int(counts.group(2)), counts.group(3) == "True"
    if not is_thread:
        raise SiteFailure("not a thread page (%d images, %d thumbnails)" % (images, thumbnails))
    if images == 0:
        raise SiteFailure("no images found (%d thumbnails)" % thumbnails)
    return images, thumbnails


def check_site(site, temp):
    index_html = fetch(site["index"], "index").decode("utf-8", errors="replace")
    thread_url = find_thread(site, index_html)
    capture_path = os.path.join(temp, site["name"] + "-raw.html")
    with open(capture_path, "wb") as f:
        f.write(fetch(thread_url, "thread"))
    return verify(site, thread_url, capture_path)


def run():
    temp = tempfile.mkdtemp(prefix="canary-", dir=os.environ.get("RUNNER_TEMP") or None)
    failed = 0
    try:
        for site in SITES:
            try:
                images, thumbnails = check_site(site, temp)
                print("%s: pass (%d images, %d thumbnails)" % (site["name"], images, thumbnails))
            except SiteFailure as e:
                failed += 1
                print("%s: FAIL, %s" % (site["name"], e))
            except Exception as e:
                # Any other error: print only its type, never its message
                failed += 1
                print("%s: FAIL, internal error (%s)" % (site["name"], type(e).__name__))
            sys.stdout.flush()
    finally:
        shutil.rmtree(temp, ignore_errors=True)
    print("%d of %d sites failed." % (failed, len(SITES)))
    return 1 if failed else 0


def dry_run():
    for site in SITES:
        print("%s (%s): fetch %s, then the first thread link matching %s" % (
            site["name"], site["helper"], site["index"], site["thread"]))
    print("Dry run: nothing fetched.")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="list the sites and what would be fetched, without network access")
    args = parser.parse_args()
    return dry_run() if args.dry_run else run()


if __name__ == "__main__":
    sys.exit(main())
