# Chan Thread Watch

This project is a fork of the discontinued Chan Thread Watch. All credit goes to the original developer.  
You can DOWNLOAD this program here: [https://github.com/kevin-contino/ChanThreadWatch/releases](https://github.com/kevin-contino/ChanThreadWatch/releases)  
You can find the original official site here: [https://sites.google.com/site/chanthreadwatch/](https://sites.google.com/site/chanthreadwatch/)

## Wiki

For documentation, changelog and any other information, please visit the wiki: [https://github.com/SuperGouge/ChanThreadWatch/wiki](https://github.com/SuperGouge/ChanThreadWatch/wiki)

## Local API in the app

The app can run the same local HTTP API as `ctw watch` (see [Local API](#local-api) below), so scripts on this computer can list the watched threads and add threads. It is off by default. Open Settings, then "Local API...":

- "Enable the local API" turns it on. "Port" is a port from 1024 to 65535 (47710 by default); another value keeps the dialog open with a message. OK applies the change at once, without a restart, and waits a moment for the start: if it fails, the dialog stays open so you can fix the port. The status line shows "Listening on 127.0.0.1:<port>", "Off", or why it could not start (for example the port is in use, or there is no token yet). A failure at start never shows a message box; it is in the status line and in `log.txt`.
- "New token..." makes the token that every request needs, and shows it once, with a Copy button. Only its hash is saved, in `api-token.txt` in the settings folder, which only your user can read. A new token replaces the old one at once. Copy keeps the token out of the Windows clipboard history and the cloud clipboard, but other clipboard tools may still keep it.
- "Allow unknown sites" also lets scripts add threads of sites the app has no support for (never IP addresses or local names).

The dialog writes `ApiEnabled`, `ApiPort` and `ApiAllowUnknownHosts` in `settings.txt`, the same keys `ctw watch` reads. The app starts the API once the thread list is loaded, adds a thread from the API as if you had added it (with the main window's current check interval, one-time download, auto-follow and category, never with a login), and stops the API first when it closes. Moving the settings folder in Settings takes `api-token.txt` along; backups never copy it. Connect to `127.0.0.1`, not `localhost`. The notes under [Local API](#local-api) apply to the app too: `settings.txt` decides whether the API is on, and the port is fixed, so another program that starts first could take it.

## Command line (ctw)

`ctw` lists, adds and removes watched threads without opening the app, and `ctw watch` watches them without a window and can run a local API for scripts. Download the file for your system from the same release as the app. Each one is a single self-contained file that needs no .NET install:

| System | File |
| --- | --- |
| Windows (most PCs) | `ctw-<version>-win-x64.exe` |
| Windows on ARM | `ctw-<version>-win-arm64.exe` |
| Linux (x64) | `ctw-<version>-linux-x64` |
| Linux (ARM64) | `ctw-<version>-linux-arm64` |
| macOS (Intel) | `ctw-<version>-osx-x64` |
| macOS (Apple silicon) | `ctw-<version>-osx-arm64` |

Rename the file to `ctw` (`ctw.exe` on Windows) if you like; the examples below use that name. Use `SHA256SUMS.txt` to check the file.

- Linux and macOS: a downloaded file is not executable. Run `chmod +x ctw` once.
- Linux: the ICU library (`libicu`, installed on most desktop systems) is needed, as for any .NET program that does not turn off culture support.
- macOS: the file is not signed with an Apple Developer ID, so Gatekeeper blocks the first run. Either right-click it in Finder, choose Open and confirm, or run `xattr -d com.apple.quarantine ctw`.

```
ctw list
ctw add <url> [--description <text>] [--category <text>]
ctw remove <url>
ctw watch
ctw api-token
ctw api-pair [--list | --remove <chrome|firefox>]
ctw --help
```

`list` prints one thread per line: the URL, category and description, separated by tabs. `add` uses the app's default check interval, one-time download and auto-follow settings, and adds no login (an address with `name:password@` is refused; set the login in the app). Unlike the app's Add button, `add` refuses a thread that is already in the list, also a stopped one. `remove` keeps the downloaded files and refuses when more than one entry is the same thread. If the removed thread's saved login is in the macOS Keychain or the Linux Secret Service, it stays there; `add` and `remove` never delete it. `add --help` and `remove --help` show a command's usage. The exit code is 0 on success, 1 on an error and 2 for an invalid command line.

`watch` loads the thread list and watches every thread as the app does, with the app's settings: it downloads images and pages to the same folders, follows threads when auto-follow is on, uses saved logins (Windows encryption, the macOS Keychain or the Linux Secret Service, as the app does), saves the thread list every minute when it changed, and backs it up when the settings ask for it. The download folder must be set in the app and exist; unlike the app, `watch` does not fall back to the default folder, it refuses to start. It writes one line for each thread added, finished, not found (404) or stopped by an error, and for each check with an error or with files that failed after all their tries; details go to `log.txt` in the settings folder, as for the app. Where no login store can be used (for example Linux without a Secret Service), it warns at start that logins saved without encryption by an older version are used for this session only and cleared at the next save, as the app does. To stop it, press Ctrl+C, send SIGTERM or SIGHUP, or close its console window (on Windows): it stops the threads, saves the thread list and exits with code 0, or with code 1 if the thread list could not be saved. A second Ctrl+C quits at once; the thread list then is the one saved last. Windows ends a program about 5 seconds after its console window is closed, so a save that takes longer is cut off there.

`ctw` uses the same settings folder as the app, and `add`, `remove`, `watch`, `api-token` and `--help` print it:

- Portable mode: put `ctw` next to `ChanThreadWatch.exe` and its `settings.txt`, or in a folder next to them. `ctw` uses the folder that holds `settings.txt`, its own or the one above it.
- Otherwise `ctw` can be anywhere, and it uses the app's folder in your application data. `list` does not create that folder. In this mode `watch` refuses to start when the download or completed folder is set relative to the app's folder, which `ctw` cannot find; set full paths in the app's settings.

`add`, `remove` and `watch` refuse while Chan Thread Watch uses that folder on this computer (`api-token` and `api-pair` do not); close the app or make the change in the app. The app refuses to start while `ctw watch` uses the folder (stop `ctw watch` first), and `add` and `remove` refuse while `ctw watch` runs. A window on another computer that was started with "start anyway" is not detected. Use Chan Thread Watch 1.40 or later with `ctw watch`: earlier versions do not check the settings folder's lock, so they would start beside it and both would save the thread list. If you start the app while `ctw add` or `ctw remove` is changing the thread list, the app waits for it. `list`, `add` and `remove` keep saved logins in the thread list as they are.

### Local API

`ctw watch` can run a local HTTP API, so scripts on this computer can list the watched threads and add threads (`GET` and `POST` on `/api/v1/threads`, described by `GET /api/v1/openapi.json`), and a browser extension can pair with it (see [Paired browsers](#paired-browsers)). It is off by default. The app and `ctw watch` use the same settings and token (see [Local API in the app](#local-api-in-the-app)). Without the app, turn it on in `settings.txt` while neither the app nor `ctw watch` runs:

```
ApiEnabled=1
ApiPort=47710
```

Only `ApiEnabled=1` turns it on; any other value but `0` leaves it off, and `ctw watch` warns. `ApiPort` must be a port from 1024 to 65535; any other value gives a warning, and port 47710 is used.

Then run `ctw api-token`. It prints a new token once, on stdout; only its hash is saved, in `api-token.txt` in the settings folder, which only your user can read. Keep the token where your scripts can read it, and send it as `Authorization: Bearer <token>`. A file you make with `ctw api-token > file` gets the default access of new files, so make it readable by you only (for example `umask 077` first, or a secret store), and never put it in a shared folder. With `ctw api-token | command`, the token is lost if the command has already exited, so redirect it to an owner-only file or read it directly. Running `ctw api-token` again replaces the token: the old one stops working at once, also in a `ctw watch` that runs, and the new one works without a restart. `api-token` works while the app or `ctw watch` runs. It refuses to run as root, as the API does.

`ctw watch` starts the API once the thread list is loaded and prints `Local API listening on http://127.0.0.1:<port>/api/v1/`. It listens on 127.0.0.1 only, and stops the API first when it stops. Connect to `127.0.0.1`, not `localhost`: the API listens on IPv4 only, and another program could listen on `[::1]` with the same port. If the port is in use, or there is no token yet, it prints a warning on stderr and watches without the API (the exit code does not change); free the port or set another `ApiPort`, or run `ctw api-token`, then start `ctw watch` again. By default the API adds threads of the supported sites only; `ApiAllowUnknownHosts=1` also allows other sites (never IP addresses or local names).

```
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:47710/api/v1/threads
curl -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"url":"<thread url>"}' http://127.0.0.1:47710/api/v1/threads
```

Limits: a request body of at most 4 KiB, 120 requests and 30 adds a minute, 10 pairing and 60 proof requests a minute (each counted apart), and 1000 threads in the list. A thread that is already in the list gets 409. Once `ctw watch` starts to stop, new connections are usually refused; a request already in progress is finished, or gets 503 if its work had not started, and one still running after the stop's wait is cut off.

Keep in mind: `settings.txt` decides whether the API is on and whether it allows other sites, so any program that can write that file can turn them on; only `api-token.txt` and `api-clients.txt` are protected. The port is fixed, so a program on this computer that starts first could listen on it and receive the token a script sends. A script can check first that it talks to the Chan Thread Watch that holds its token: `POST /api/v1/proof` with `{"clientNonce":"<32 random bytes, base64url>"}`, no token and no `Origin` answers `{"proof":"..."}`, which must equal HMAC-SHA256 keyed with SHA-256 of the token over the lines `ctw-proof-v1`, `127.0.0.1:<port>`, an empty line and the nonce, joined with line feeds (base64url without padding). Only a program that has the token's hash can make it, and a proof for another port does not match. Send the token only if it matches:

```
import base64, hashlib, hmac, json, os, urllib.request
b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()
nonce = b64(os.urandom(32))
request = urllib.request.Request("http://127.0.0.1:47710/api/v1/proof", json.dumps({"clientNonce": nonce}).encode(), {"Content-Type": "application/json"})
proof = json.load(urllib.request.urlopen(request))["proof"]
expected = b64(hmac.new(hashlib.sha256(token.encode()).digest(), ("ctw-proof-v1\n127.0.0.1:47710\n\n" + nonce).encode(), hashlib.sha256).digest())
assert hmac.compare_digest(proof, expected)
```

#### Paired browsers

A browser extension does not use the scripts' token. It pairs once with an 8-symbol code that Chan Thread Watch shows for 5 minutes (`POST /api/v1/pairing`, described in `openapi.json`). This route and `POST /api/v1/proof` are the only ones without a token, and only in that exact spelling: another case, an encoded letter or a trailing slash gets 401. The code itself never goes over the wire: the program proves first that it knows the code, then the extension, so another program on the port gets neither the code nor a token. Each code allows three tries (a mistyped code uses one); then make a new code. Pairing adds a token for that browser, one for Chrome and one for Firefox, and never changes the scripts' token; pairing the same browser again replaces its older token at once. Only the hashes are saved, in `api-clients.txt` in the settings folder, which only your user can read. A pending code is kept as a stretched key, never the code, in `api-pairing.txt`. A code that paired a browser is marked as used there, and the program that showed the code removes the file later; a code that ended (wrong tries, expired) is removed when a pairing request meets it, or by the program that showed it. The program that showed a code and the API both write this file without a lock between them, so a program that makes codes compares the file's `id` line with its own code before it reports "paired" or "ended", and deletes the file only while it holds its own code. Backups never copy either file.

A browser's token works only with the `Origin` the browser sent when it paired, and the scripts' token never works with a browser extension's `Origin` (both get 403). Before each use, the extension asks `POST /api/v1/proof` with its `Origin` and sends its token only if the proof matches. Any program on this computer can use up the pairing and proof limits with well-formed requests; that only delays pairing or a check, and gains it nothing. If `api-clients.txt` can't be used (it is damaged, a link, or others can read it), no paired browser can connect, the log says so, and the scripts' token still works; pairing again writes a new file. The API still needs the scripts' token to start.

To pair with `ctw watch`, run `ctw api-pair` while `ctw watch` runs the API (or before, as long as the API starts within the code's 5 minutes). It prints the code once, on stdout, as `XXXX-XXXX`, and waits; enter the code in the extension's options. It prints `Paired with the Chrome extension` (or Firefox) and exits with code 0 once a browser paired, or says why the code ended (it expired, wrong codes used up its tries, `api-pairing.txt` was deleted or changed by another program, or a newer code from another program that makes codes, such as the app or another `ctw api-pair`, replaced it) and exits with code 1. Ctrl+C cancels the code (exit code 1); a second Ctrl+C quits at once. When it ends, it deletes `api-pairing.txt`, unless the file holds another code by then or was changed by another program (if it can't delete the file, it warns that the code works until it expires). It warns when `settings.txt` does not have `ApiEnabled=1` or when there is no `api-token.txt` (`ctw watch` does not start the API without a token; run `ctw api-token` first), but still makes the code. If `settings.txt` can't be read, it exits with code 1 and makes no code. `ctw api-pair --list` prints the paired browsers, one per line: `chrome` or `firefox`, a tab, and when it paired (UTC); it never prints a token or a hash. `ctw api-pair --remove chrome` (or `firefox`) unpairs that browser: its token stops working at once, also in a `ctw watch` or app that runs. Another name after `--remove` (the names are lowercase) is an invalid command line (exit code 2). Removing a browser that is not paired is an error (exit code 1), and so is `--list` or `--remove` when `api-clients.txt` can't be used (it could not be read, or it is damaged, a link, or others can read it). `--list` and `--remove` make no code. `api-pair` works while the app or `ctw watch` runs, and refuses to run as root, as the API does.

The API adds threads that never connect to a local or private address (loopback, your LAN, link-local addresses such as cloud metadata, and the like), on any redirect either, and that refuse to connect through a system proxy; threads they auto-follow get the same limit. The app and `ctw watch` keep the list of these threads in `api-threads.txt` next to `threads.txt`, and `ctw add` and `ctw remove` keep it up to date. Keep the two files together when you copy or restore the settings folder: a thread list backup (`threads.txt.bak`) comes with `api-threads.txt.bak`, so restore both. If `api-threads.txt` can't be used at start, the threads loaded from the thread list, and the threads they auto-follow, are limited this way until the app or `ctw watch` stops (the log says so, and `ctw watch` warns at start); a file that is not valid is kept aside. Once `api-threads.txt` has been written, `settings.txt` says so (`ApiThreadsFileWritten=1`), and a missing `api-threads.txt` is then treated the same way, so keep both files when you restore a backup or copy the settings folder. That setting lives in `settings.txt`, so replacing `settings.txt`, or a save by a copy of the app on another computer started with "start anyway", can drop it. Chan Thread Watch 1.39.0 and earlier ignore `api-threads.txt` and do not limit these threads, so while you run such a version they can connect anywhere; the limit applies again when you start 1.40.0 or later.

## Browser extension

The browser extension in `tools/browser-extension` adds threads to Chan Thread Watch from Chrome or Firefox through the local API. Right-click a thread link or a thread page and choose "Watch this thread", or open its toolbar button and choose "Watch this tab" or "Watch all chan tabs". The toolbar button shows `+` (added), `=` (already watched) or `!` (an error) for 4 seconds, and its popup shows the last result and whether the extension is paired. "Watch all chan tabs" first asks for permission to read the tabs of the supported sites, then adds every thread tab after one check, and says how many threads it added, how many were already watched, skipped (not a thread) or failed. The API adds at most 30 threads a minute, so it stops at that limit and says how long to wait for the rest. It also stops when the app is busy or closing, when it can no longer be reached, or when the pairing no longer works, and says how many threads it did not send. An add that times out counts as failed and the rest are still sent, as the app may have added that thread.

It is not in a browser store. Build it with Node.js 20 or later (its tests need 20.11 or later), from `tools/browser-extension`:

```
node scripts/package.mjs
```

This writes `dist/chrome` and `dist/firefox` (`--out <folder>` writes them elsewhere). Each is a copy of `src` with the manifest for that browser and the app's version; nothing is bundled or changed.

- Chrome: open `chrome://extensions`, turn on "Developer mode", choose "Load unpacked" and select `dist/chrome`. Its id is always `eifjifphdncjkkolefjepjhdlmcdbndh`, the only Chrome extension the API pairs with.
- Firefox: open `about:debugging#/runtime/this-firefox`, choose "Load Temporary Add-on..." and select `dist/firefox/manifest.json`. Firefox removes a temporary add-on when it closes, so load it again (and pair again if it says "Not paired") after each restart.

To pair, turn on the local API (see [Local API in the app](#local-api-in-the-app) or [Local API](#local-api)), then:

1. In the app, open Settings, "Local API...", and choose "Pair extension..." while the status is "Listening". With `ctw watch`, run `ctw api-pair` instead. Either shows an 8-symbol code (`XXXX-XXXX`) for 5 minutes.
2. In the extension's options (the popup's "Options" button), check the port (47710 by default), type the code and choose "Pair".
3. The options page asks "Pair with <name> at 127.0.0.1:<port>?". The name is the one the app or `ctw watch` shows; choose "Pair" to save the pairing, or "Cancel".

A wrong code says so and sends nothing more; each code allows three tries. While the extension is paired, changing the port in the options checks that the paired Chan Thread Watch answers there before it is saved; a port it refuses is set back to the saved one. While it is not paired, the port is saved without a check. "Forget pairing" deletes the extension's own copy of its token; to unpair a browser in Chan Thread Watch, use "Unpair" in the dialog or `ctw api-pair --remove <chrome|firefox>`.

Privacy: the extension talks only to `127.0.0.1` on the port you set, never to another server, and asks for no other access unless you use "Watch all chan tabs". It has no content scripts and does not read pages. Once per click (once for all the threads of "Watch all chan tabs"), it checks that the program on the port is the Chan Thread Watch it paired with, and sends its token only then. The token is kept in the browser's local extension storage (never synced) and is never shown.

In some browsers the permission prompt of "Watch all chan tabs" closes the popup. If nothing happens after you allow it, choose "Watch all chan tabs" again.

## Building and testing

Requires the .NET 10 SDK. The app and its tests build only on Windows; see below for Linux and macOS. The app targets `net10.0-windows`; `ChanThreadWatch.Core` targets `net10.0`.

```
dotnet build ChanThreadWatch.sln -c Release
dotnet test ChanThreadWatch.sln -c Release --no-build
```

The second command runs every test, including the UI smoke test in `ChanThreadWatch.UITests`. That test launches the built app, so it needs an interactive desktop session. Each run uses a copy of the exe in a temporary folder with its own settings, so it does not change your own settings or thread list. To skip it, add `--filter "TestCategory!=UI"`.

On Linux and macOS only `ChanThreadWatch.Core`, the command line `ChanThreadWatch.Cli`, the local API library `ChanThreadWatch.Api` and their tests build. Run them without the UI tests and the tests marked `PendingUnix`. Those tests cover Windows-only behavior that is not ported yet, and each one's comment gives the reason.

```
dotnet test ChanThreadWatch.Core.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
dotnet test ChanThreadWatch.Cli.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
dotnet test ChanThreadWatch.Api.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
```

The browser extension's tests need only Node.js 20.11 or later and no packages: `npm ci --ignore-scripts` and `npm test` in `tools/browser-extension` (see its `README.md`).

CI runs the Core, command line and API tests on Windows, Ubuntu and macOS (the "Core tests" check), and the full Windows build and tests in "Windows build and test". The required checks "Build and test" and "Core tests" pass when those jobs pass, or when a pull request changes only documentation and the jobs are skipped.

The release exes are self-contained single files, one per architecture: `pwsh .github/scripts/publish-app.ps1` writes `publish/ChanThreadWatch-win-x64.exe` and `publish/ChanThreadWatch-win-arm64.exe`. `pwsh .github/scripts/publish-cli.ps1` writes the command line files `publish/ctw-<version>-<rid>` for Windows and Linux (`-RuntimeIdentifiers` picks them; the macOS files are published, and signed ad hoc, on macOS only); `ctw` takes its version from the app's `Properties/AssemblyInfo.cs`.

A weekly live canary (`.github/workflows/canary.yml`) fetches one current thread per supported site and checks that its site helper still parses it, using the fixture sanitizer in verify mode. It fails when a site changes its markup, and also on fetch errors (shown as `fetch failed (index): HTTP <code>` or `fetch failed (thread): ...`, so an outage is distinguishable from a markup change). To run it locally after a Release build: `python tools/site-fixtures/live_canary.py` (add `--dry-run` to list the sites without fetching), which also needs PowerShell 7.6 or later (`pwsh`) on the PATH.

## License

Chan Thread Watch was written by J.D. Purcell (JDP) and is licensed under the MIT License, as published in the original repository ([jdpurcell/ChanThreadWatch](https://github.com/jdpurcell/ChanThreadWatch)). See [LICENSE.txt](LICENSE.txt).
