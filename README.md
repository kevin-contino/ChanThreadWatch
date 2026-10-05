# Chan Thread Watch

This project is a fork of the discontinued Chan Thread Watch. All credit goes to the original developer.  
You can DOWNLOAD this program here: [https://github.com/kevin-contino/ChanThreadWatch/releases](https://github.com/kevin-contino/ChanThreadWatch/releases)  
You can find the original official site here: [https://sites.google.com/site/chanthreadwatch/](https://sites.google.com/site/chanthreadwatch/)

## Wiki

For documentation, changelog and any other information, please visit the wiki: [https://github.com/SuperGouge/ChanThreadWatch/wiki](https://github.com/SuperGouge/ChanThreadWatch/wiki)

## Command line (ctw)

`ctw` lists, adds and removes watched threads without opening the app. Download `ctw-<version>.zip` from the same release as the app. It runs on Windows, Linux and macOS and needs the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The zip holds a `ctw` folder.

```
dotnet ctw/ctw.dll list
dotnet ctw/ctw.dll add <url> [--description <text>] [--category <text>]
dotnet ctw/ctw.dll remove <url>
dotnet ctw/ctw.dll --help
```

`list` prints one thread per line: the URL, category and description, separated by tabs. `add` uses the app's default check interval, one-time download and auto-follow settings, and adds no login (an address with `name:password@` is refused; set the login in the app). Unlike the app's Add button, `add` refuses a thread that is already in the list, also a stopped one. `remove` keeps the downloaded files and refuses when more than one entry is the same thread. If the removed thread's saved login is in the macOS Keychain or the Linux Secret Service, it stays there; `ctw` never deletes it. `add --help` and `remove --help` show a command's usage. The exit code is 0 on success, 1 on an error and 2 for an invalid command line.

`ctw` uses the same settings folder as the app, and `add`, `remove` and `--help` print it:

- Portable mode: unzip so that the `ctw` folder sits next to `ChanThreadWatch.exe` and its `settings.txt`. `ctw` uses the folder that holds `settings.txt`, its own or the one above it.
- Otherwise `ctw` can be anywhere, and it uses the app's folder in your application data. `list` does not create that folder.

`add` and `remove` refuse while Chan Thread Watch uses that folder on this computer; close the app or make the change in the app. A window on another computer that was started with "start anyway" is not detected. If you start the app while `ctw` is changing the thread list, the app waits for it. Saved logins in the thread list are kept as they are.

## Building and testing

Requires the .NET 10 SDK. The app and its tests build only on Windows; see below for Linux and macOS. The app targets `net10.0-windows`; `ChanThreadWatch.Core` targets `net10.0`.

```
dotnet build ChanThreadWatch.sln -c Release
dotnet test ChanThreadWatch.sln -c Release --no-build
```

The second command runs every test, including the UI smoke test in `ChanThreadWatch.UITests`. That test launches the built app, so it needs an interactive desktop session. Each run uses a copy of the exe in a temporary folder with its own settings, so it does not change your own settings or thread list. To skip it, add `--filter "TestCategory!=UI"`.

On Linux and macOS only `ChanThreadWatch.Core`, the command line `ChanThreadWatch.Cli` and their tests build. Run them without the UI tests and the tests marked `PendingUnix`. Those tests cover Windows-only behavior that is not ported yet, and each one's comment gives the reason.

```
dotnet test ChanThreadWatch.Core.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
dotnet test ChanThreadWatch.Cli.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
```

CI runs the Core and command line tests on Windows, Ubuntu and macOS (the "Core tests" check), and the full Windows build and tests in "Windows build and test". The required checks "Build and test" and "Core tests" pass when those jobs pass, or when a pull request changes only documentation and the jobs are skipped.

The release exes are self-contained single files, one per architecture: `pwsh .github/scripts/publish-app.ps1` writes `publish/ChanThreadWatch-win-x64.exe` and `publish/ChanThreadWatch-win-arm64.exe`. `pwsh .github/scripts/publish-cli.ps1` writes the command line zip `publish/ctw-<version>.zip`; `ctw` takes its version from the app's `Properties/AssemblyInfo.cs`.

A weekly live canary (`.github/workflows/canary.yml`) fetches one current thread per supported site and checks that its site helper still parses it, using the fixture sanitizer in verify mode. It fails when a site changes its markup, and also on fetch errors (shown as `fetch failed (index): HTTP <code>` or `fetch failed (thread): ...`, so an outage is distinguishable from a markup change). To run it locally after a Release build: `python tools/site-fixtures/live_canary.py` (add `--dry-run` to list the sites without fetching), which also needs PowerShell 7.6 or later (`pwsh`) on the PATH.

## License

Chan Thread Watch was written by J.D. Purcell (JDP) and is licensed under the MIT License, as published in the original repository ([jdpurcell/ChanThreadWatch](https://github.com/jdpurcell/ChanThreadWatch)). See [LICENSE.txt](LICENSE.txt).

