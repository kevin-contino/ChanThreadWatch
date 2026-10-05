---
name: sanitize-site-fixture
description: Capture a real thread page from a supported imageboard and turn it into a sterile test fixture in ChanThreadWatch.Tests/Fixtures/sites. Use when adding or refreshing a site fixture, or when a site's markup changed.
---

# Sanitize a site fixture

Site fixtures are real thread markup with every value replaced by a placeholder. The repository is public: **no real data may ever be committed**, not even in an unpushed commit.

## Rules

- Keep raw captures outside the repository (use the session scratchpad). Never stage them. Delete them when the fixture is approved.
- Pick a thread on a work-safe board (for example /wg/, /g/, /ck/, /tech/).
- Capture directly first, with a browser user agent. The app downloads server HTML without running scripts, and a direct capture matches that.
- Use Firecrawl only when a direct capture is blocked (for example a Cloudflare 403). Its `rawHtml` is the page after scripts ran, so note `--capture firecrawl`.
- Run the `firecrawl` CLI from the scratchpad, never from the repository (it writes a `.firecrawl/` cache to the working directory). It authenticates with its own stored credentials. Never print, log or commit a Firecrawl key.
- Never edit a fixture by hand. Change `allowlist.json` or the sanitizer instead, so the same rules apply to every fixture.

## Steps

1. **Build** the app in Release, because the sanitizer runs the real site helpers from `src/ChanThreadWatch/bin/Release/ChanThreadWatch.Core.dll`. Use `dotnet build`, as described in the README. The sanitizer loads the .NET 10 `ChanThreadWatch.Core.dll` through PowerShell 7 (`pwsh`).
2. **Capture** the thread into the scratchpad:
   - Direct: `curl -s -A "<browser user agent>" "<thread URL>" -o <scratchpad>/<name>-raw.html`
   - Blocked: `cd <scratchpad> && firecrawl scrape "<thread URL>" -f rawHtml -o <name>-raw.html`
3. **Dry run**, which writes nothing:
   `python tools/site-fixtures/sanitize_site_fixture.py --raw <scratchpad>/<name>-raw.html --url "<thread URL>" --helper <SiteHelper class> --name <fixture name> --capture direct|firecrawl --captured <YYYY-MM-DD> --dry-run`
   - It runs the helper on the capture and on the sanitized result, and fails unless the images, file names, posters, hashes, thumbnails and cross links correspond one to one.
   - If it fails, find which markup the helper reads that the allowlist drops. Extend `allowlist.json` or the sanitizer, then run it again.
4. **Write** the fixture: run the same command without `--dry-run`. It writes `<name>.html` and its `manifest.json` entry (helper, page path, capture method and date, expected counts).
5. **Test**: build, then `dotnet test ChanThreadWatch.Core.Tests/ChanThreadWatch.Core.Tests.csproj --no-build -c Release --filter FullyQualifiedName~SiteFixtureTests`. `FixtureIsSterile` fails on any tag, class, attribute, value or text outside the allowlist and the placeholder grammar.
6. **Diff**: `git status --untracked-files=all -- ChanThreadWatch.Tests/Fixtures/sites` lists new fixtures, and `git diff -- ChanThreadWatch.Tests/Fixtures/sites` shows changes to committed ones. Summarize the structural change for the maintainer.
7. **Human review**: show the maintainer the new or changed fixtures and wait for approval before committing.
8. **Commit** by explicit path only (`git add ChanThreadWatch.Tests/Fixtures/sites/<name>.html ChanThreadWatch.Tests/Fixtures/sites/manifest.json`), never `git add -A` or `git add .`. Check `git status` first: nothing named `*-raw.html` or outside the fixture files may be staged.
9. **Delete** the raw captures from the scratchpad.

## Placeholders

| Real value | Placeholder |
|---|---|
| Post, thread and other numbers | `777` plus 7 digits |
| Board names and other URL words | `w1`, `w2`, ... (allowlisted keywords such as `thread`, `res`, `src` stay) |
| Hosts | `{{base}}` (the page host) or `{{media}}` (any other host) |
| Hex file hashes | `fff...000001` |
| MD5s | `{{md5_N}}`, URL-safe form `{{md5u_N}}`, standard form in a `/image/` path `{{md5s_N}}` |
| File names | `file-N.ext` |
| Names, tripcodes, poster IDs | `name-N`, `!trip-N`, `id-N` / `ID:id-N` ("Anonymous" stays) |

The tests replace the hosts with `http://fixture.test` and `http://media.test`, and `{{md5_N}}` with the base64 MD5 of `fixture-image-N-K`, where K is the smallest number from 0 whose base64 MD5 contains both `/` and `+`. `{{md5u_N}}` and `{{md5s_N}}` are the same MD5 in URL-safe and standard base64, without padding.
