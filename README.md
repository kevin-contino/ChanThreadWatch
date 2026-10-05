# Chan Thread Watch

This project is a fork of the discontinued Chan Thread Watch. All credit goes to the original developer.  
You can DOWNLOAD this program here: [https://github.com/kevin-contino/ChanThreadWatch/releases](https://github.com/kevin-contino/ChanThreadWatch/releases)  
You can find the original official site here: [https://sites.google.com/site/chanthreadwatch/](https://sites.google.com/site/chanthreadwatch/)

## Wiki

For documentation, changelog and any other information, please visit the wiki: [https://github.com/SuperGouge/ChanThreadWatch/wiki](https://github.com/SuperGouge/ChanThreadWatch/wiki)

## Building and testing

Requires the .NET 10 SDK. The app and its tests build only on Windows; see below for Linux and macOS. The app targets `net10.0-windows`; `ChanThreadWatch.Core` targets `net10.0`.

```
dotnet build ChanThreadWatch.sln -c Release
dotnet test ChanThreadWatch.sln -c Release --no-build
```

The second command runs every test, including the UI smoke test in `ChanThreadWatch.UITests`. That test launches the built app, so it needs an interactive desktop session. Each run uses a copy of the exe in a temporary folder with its own settings, so it does not change your own settings or thread list. To skip it, add `--filter "TestCategory!=UI"`.

On Linux and macOS only `ChanThreadWatch.Core` and its tests build. Run them without the UI tests and the tests marked `PendingUnix`. Those tests cover Windows-only behavior that is not ported yet, and each one's comment gives the reason.

```
dotnet test ChanThreadWatch.Core.Tests -c Release --filter "TestCategory!=PendingUnix&TestCategory!=UI"
```

CI runs the Core tests on Windows, Ubuntu and macOS (the "Core tests" check), and the full Windows build and tests in "Windows build and test". The required checks "Build and test" and "Core tests" pass when those jobs pass, or when a pull request changes only documentation and the jobs are skipped.

The release exes are self-contained single files, one per architecture: `pwsh .github/scripts/publish-app.ps1` writes `publish/ChanThreadWatch-win-x64.exe` and `publish/ChanThreadWatch-win-arm64.exe`.

A weekly live canary (`.github/workflows/canary.yml`) fetches one current thread per supported site and checks that its site helper still parses it, using the fixture sanitizer in verify mode. It fails when a site changes its markup, and also on fetch errors (shown as `fetch failed (index): HTTP <code>` or `fetch failed (thread): ...`, so an outage is distinguishable from a markup change). To run it locally after a Release build: `python tools/site-fixtures/live_canary.py` (add `--dry-run` to list the sites without fetching), which also needs PowerShell 7.6 or later (`pwsh`) on the PATH.

## License

Chan Thread Watch was written by J.D. Purcell (JDP) and is licensed under the MIT License, as published in the original repository ([jdpurcell/ChanThreadWatch](https://github.com/jdpurcell/ChanThreadWatch)). See [LICENSE.txt](LICENSE.txt).
