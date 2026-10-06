# Chan Thread Watch

This project is a fork of the discontinued Chan Thread Watch. All credit goes to the original developer.  
You can DOWNLOAD this program here: [https://github.com/kevin-contino/ChanThreadWatch/releases](https://github.com/kevin-contino/ChanThreadWatch/releases)  
You can find the original official site here: [https://sites.google.com/site/chanthreadwatch/](https://sites.google.com/site/chanthreadwatch/)

## Wiki

For documentation, changelog and any other information, please visit the wiki: [https://github.com/SuperGouge/ChanThreadWatch/wiki](https://github.com/SuperGouge/ChanThreadWatch/wiki)

## Command line (ctw)

`ctw` lists, adds and removes watched threads without opening the app, and `ctw watch` watches them without a window. Download the file for your system from the same release as the app. Each one is a single self-contained file that needs no .NET install:

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
ctw --help
```

`list` prints one thread per line: the URL, category and description, separated by tabs. `add` uses the app's default check interval, one-time download and auto-follow settings, and adds no login (an address with `name:password@` is refused; set the login in the app). Unlike the app's Add button, `add` refuses a thread that is already in the list, also a stopped one. `remove` keeps the downloaded files and refuses when more than one entry is the same thread. If the removed thread's saved login is in the macOS Keychain or the Linux Secret Service, it stays there; `add` and `remove` never delete it. `add --help` and `remove --help` show a command's usage. The exit code is 0 on success, 1 on an error and 2 for an invalid command line.

`watch` loads the thread list and watches every thread as the app does, with the app's settings: it downloads images and pages to the same folders, follows threads when auto-follow is on, uses saved logins (Windows encryption, the macOS Keychain or the Linux Secret Service, as the app does), saves the thread list every minute when it changed, and backs it up when the settings ask for it. The download folder must be set in the app and exist; unlike the app, `watch` does not fall back to the default folder, it refuses to start. It writes one line for each thread added, finished, not found (404) or stopped by an error, and for each check with an error or with files that failed after all their tries; details go to `log.txt` in the settings folder, as for the app. Where no login store can be used (for example Linux without a Secret Service), it warns at start that logins saved without encryption by an older version are used for this session only and cleared at the next save, as the app does. To stop it, press Ctrl+C, send SIGTERM or SIGHUP, or close its console window (on Windows): it stops the threads, saves the thread list and exits with code 0, or with code 1 if the thread list could not be saved. A second Ctrl+C quits at once; the thread list then is the one saved last. Windows ends a program about 5 seconds after its console window is closed, so a save that takes longer is cut off there.

`ctw` uses the same settings folder as the app, and `add`, `remove`, `watch` and `--help` print it:

- Portable mode: put `ctw` next to `ChanThreadWatch.exe` and its `settings.txt`, or in a folder next to them. `ctw` uses the folder that holds `settings.txt`, its own or the one above it.
- Otherwise `ctw` can be anywhere, and it uses the app's folder in your application data. `list` does not create that folder. In this mode `watch` refuses to start when the download or completed folder is set relative to the app's folder, which `ctw` cannot find; set full paths in the app's settings.

`add`, `remove` and `watch` refuse while Chan Thread Watch uses that folder on this computer; close the app or make the change in the app. The app refuses to start while `ctw watch` uses the folder (stop `ctw watch` first), and `add` and `remove` refuse while `ctw watch` runs. A window on another computer that was started with "start anyway" is not detected. Use Chan Thread Watch 1.40 or later with `ctw watch`: earlier versions do not check the settings folder's lock, so they would start beside it and both would save the thread list. If you start the app while `ctw add` or `ctw remove` is changing the thread list, the app waits for it. `list`, `add` and `remove` keep saved logins in the thread list as they are.

A local API (coming in a later release; it is not available in the app or `ctw` yet) will add threads that never connect to a local or private address (loopback, your LAN, link-local addresses such as cloud metadata, and the like), on any redirect either, and that refuse to connect through a system proxy; threads they auto-follow get the same limit. The app and `ctw watch` keep the list of these threads in `api-threads.txt` next to `threads.txt`, and `ctw add` and `ctw remove` keep it up to date. Keep the two files together when you copy or restore the settings folder: a thread list backup (`threads.txt.bak`) comes with `api-threads.txt.bak`, so restore both. If `api-threads.txt` can't be used at start, the threads loaded from the thread list, and the threads they auto-follow, are limited this way until the app or `ctw watch` stops (the log says so, and `ctw watch` warns at start); a file that is not valid is kept aside. Once `api-threads.txt` has been written, `settings.txt` says so (`ApiThreadsFileWritten=1`), and a missing `api-threads.txt` is then treated the same way, so keep both files when you restore a backup or copy the settings folder. That setting lives in `settings.txt`, so replacing `settings.txt`, or a save by a copy of the app on another computer started with "start anyway", can drop it. Chan Thread Watch 1.39.0 and earlier ignore `api-threads.txt` and do not limit these threads, so while you run such a version they can connect anywhere; the limit applies again when you start 1.40.0 or later.

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

CI runs the Core, command line and API tests on Windows, Ubuntu and macOS (the "Core tests" check), and the full Windows build and tests in "Windows build and test". The required checks "Build and test" and "Core tests" pass when those jobs pass, or when a pull request changes only documentation and the jobs are skipped.

The release exes are self-contained single files, one per architecture: `pwsh .github/scripts/publish-app.ps1` writes `publish/ChanThreadWatch-win-x64.exe` and `publish/ChanThreadWatch-win-arm64.exe`. `pwsh .github/scripts/publish-cli.ps1` writes the command line files `publish/ctw-<version>-<rid>` for Windows and Linux (`-RuntimeIdentifiers` picks them; the macOS files are published, and signed ad hoc, on macOS only); `ctw` takes its version from the app's `Properties/AssemblyInfo.cs`.

A weekly live canary (`.github/workflows/canary.yml`) fetches one current thread per supported site and checks that its site helper still parses it, using the fixture sanitizer in verify mode. It fails when a site changes its markup, and also on fetch errors (shown as `fetch failed (index): HTTP <code>` or `fetch failed (thread): ...`, so an outage is distinguishable from a markup change). To run it locally after a Release build: `python tools/site-fixtures/live_canary.py` (add `--dry-run` to list the sites without fetching), which also needs PowerShell 7.6 or later (`pwsh`) on the PATH.

## License

Chan Thread Watch was written by J.D. Purcell (JDP) and is licensed under the MIT License, as published in the original repository ([jdpurcell/ChanThreadWatch](https://github.com/jdpurcell/ChanThreadWatch)). See [LICENSE.txt](LICENSE.txt).
