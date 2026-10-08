# Chan Thread Watch browser extension

Adds threads to Chan Thread Watch through its local API on `127.0.0.1` (see "Browser extension" in the repository's `README.md` for use and pairing). Manifest V3, one source folder for Chrome and Firefox, ES modules, no dependencies, no bundler.

## Build

Needs Node.js 20 or later; the tests need 20.11 or later.

```
node scripts/package.mjs [--out <folder>] [--dry-run]
```

Writes `dist/chrome` and `dist/firefox` (or `<folder>/chrome` and `<folder>/firefox`), and removes nothing else in the folder: a copy of `src/` and a `manifest.json` made from `manifest.base.json` plus `manifest.chrome.json` or `manifest.firefox.json`. It adds `optional_host_permissions` from `known-hosts.json` and the version `Major.Minor.Revision` from `src/ChanThreadWatch/Properties/AssemblyInfo.cs`. An overlay may not set a key of the base, `version` or `optional_host_permissions`. `--dry-run` prints the folders it would remove and write, and writes nothing. `dist/` is git-ignored.

`known-hosts.json` is a copy of the domains of the site helpers (`SiteHelpers._siteHelpers` in `ChanThreadWatch.Core`), edited by hand. The Core test `ExtensionKnownHostsMatchSiteHelpers` fails when they differ. The Chrome `key` in `manifest.chrome.json` is a public key; it fixes the extension id that the API accepts (`ApiPairing.ChromeExtensionId`, checked by `ChromeExtensionIdMatchesManifestKey` in `ChanThreadWatch.Api.Tests`).

## Test

```
npm ci --ignore-scripts
npm test
```

The tests use `node:test` with in-memory fakes of the browser and of the API (`test/fakes.mjs`); no browser is started and nothing goes over the network. They read `ChanThreadWatch.Api.Tests/PairingVectors.json`, the pairing values the C# tests check too. `npm test` runs `node --test`, which also loads `test/fakes.mjs` as a file without tests. The badge timer runs on mock time. `test/manifest.test.mjs` builds into a temporary folder. `test/source-scan.test.mjs` refuses `eval`, string timers, HTML from strings, `console`, `storage.sync` and any address other than `http://127.0.0.1`, and runs `node --check` on every script.

CI runs both in the "Browser extension" job and uploads the two folders (not for a pull request from a fork). Lizard checks every function in `src` and `scripts` at CCN 5 or less (`lizard -l javascript -C 5 -w src scripts`).

## Files

| File | What it does |
| --- | --- |
| `src/background.js` | Context menu, the adds, the badge and the popup's status |
| `src/api.js` | Every request to the API: proof before use, adds, pairing, storage of the pairing |
| `src/pairing.js` | Pairing protocol v1: code, PBKDF2 key, HMAC proofs, checks of the answers |
| `src/urls.js` | Thread address filter and the host and menu patterns |
| `src/errors.js` | The texts shown for each failure |
| `src/browser.js` | The browser's extension API (`browser` or `chrome`) and the extension's Origin |
| `src/popup.html`, `src/popup.js` | "Watch this tab", "Watch all chan tabs", status and last result |
| `src/options.html`, `src/options.js` | Port, pairing with a code, "Forget pairing" |
| `src/icons/` | The app icon as PNG (16, 32 and 48 from `ChanThreadWatch.ico`'s own sizes, 128 from its 256 image) |
