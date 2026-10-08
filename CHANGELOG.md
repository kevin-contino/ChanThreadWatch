# Changelog

User-visible changes to Chan Thread Watch. Each merged change that users can see, or that changes how the app behaves, raises the minor version by one.

## v1.40.0 (unreleased)

### Before you upgrade

- The download is now one self-contained exe per processor type: `ChanThreadWatch-win-x64.exe` for most PCs and `ChanThreadWatch-win-arm64.exe` for Windows on ARM. It no longer needs .NET Framework or any other .NET install, so the file is much larger (about 64 MB). Use `SHA256SUMS.txt` to check the files.
- Windows 10 (version 1607 or later) or Windows 11 is required. On Windows on ARM, `ChanThreadWatch-win-arm64.exe` needs Windows 11, or Windows 10 version 21H2 or later, as listed in the .NET 10 supported systems. 32-bit Windows, Windows 7 and Windows 8.1 are no longer supported.
- To upgrade, put the new exe in the folder of the old one. Your settings, thread list and saved logins carry over, also when the new exe has a different name. You can then delete the old `ChanThreadWatch.exe`, `ChanThreadWatch.exe.config` and `ChanThreadWatch.Core.dll`.

### Added

- A command line tool, `ctw`, to list, add and remove watched threads without opening the app: `ctw list`, `ctw add <url> [--description <text>] [--category <text>]` and `ctw remove <url>`. It comes as one self-contained file per system, which needs no .NET install: `ctw-1.40.0-win-x64.exe`, `ctw-1.40.0-win-arm64.exe`, `ctw-1.40.0-linux-x64`, `ctw-1.40.0-linux-arm64`, `ctw-1.40.0-osx-x64` and `ctw-1.40.0-osx-arm64`. On Linux and macOS, run `chmod +x` on the file first; on macOS, the file is not signed with an Apple Developer ID, so open it once with right-click, Open (or remove the quarantine attribute with `xattr`). For portable mode, put it next to `ChanThreadWatch.exe`, or in a folder next to it. It uses the app's settings folder and thread list, and refuses to add or remove while the app uses that folder on this computer (a window on another computer that was started with "start anyway" is not detected). New threads get the app's default check interval, one-time download and auto-follow settings, and no login. Unlike the app, it refuses a thread that is already in the list. Removing a thread keeps its downloaded files, and a saved login in the macOS Keychain or Linux Secret Service stays there.
- `ctw watch` watches the threads of the thread list without a window, with the app's settings and saved logins, until you press Ctrl+C, send SIGTERM or SIGHUP, or close its console window. It downloads to the same folders (it refuses to start when the download folder is not set or does not exist), saves the thread list every minute when it changed and again when it stops, and prints a line for each thread that is added, finishes, is not found or stops on an error, and for each check with an error or with files that failed after all their tries. Use it with Chan Thread Watch 1.40 or later, which checks the settings folder's lock. While it runs, the app and `ctw add` and `ctw remove` refuse to use the settings folder and say that `ctw watch` is using it, and `ctw watch` refuses to start while the app uses the folder on this computer.
- `ctw watch` can run a local API, so scripts on this computer can list the watched threads and add threads over HTTP (`GET` and `POST` on `http://127.0.0.1:47710/api/v1/threads`). It is off unless `settings.txt` has `ApiEnabled=1`, listens on 127.0.0.1 only, on `ApiPort` (47710 by default), and starts once the thread list is loaded. Every request needs the token from `ctw api-token`. A thread added this way, and the threads it auto-follows, never connect to a local or private address. When the port is in use or there is no token yet, `ctw watch` warns and goes on without the API. The API makes the `ctw` files larger: about 12.6 MB more on Windows x64, which is now about 50 MB.
- The app can run the same local API, so scripts can list and add threads while the app runs. It is off by default: turn it on in Settings, "Local API...", which also sets the port, makes the token (shown once, with a Copy button that keeps it out of the Windows clipboard history) and can allow sites the app has no support for. Changes apply without a restart, and the dialog shows whether the API listens or why it could not start (it never shows a message at start). The app starts the API once the thread list is loaded and stops it first when it closes. A thread added through the API gets the main window's current check interval, one-time download, auto-follow and category, never a login, and appears in the list like any other; it and the threads it auto-follows never connect to a local or private address. Moving the settings folder takes `api-token.txt` along, and backups never copy it. The API makes the app's exe about 12 MB larger (about 64 MB for x64).
- `ctw api-token` makes the local API's token and prints it once. Only its hash is saved, in `api-token.txt` in the settings folder, which only your user can read. Running it again replaces the token at once, also for a `ctw watch` that runs.
- `ctw api-pair` is the command that pairs a browser extension with the local API. It prints an 8-symbol code once (`XXXX-XXXX`, valid for 5 minutes), waits until a browser extension pairs with it, and says "Paired with the Chrome extension" (or Firefox), or why the code ended. Ctrl+C cancels the code. Only a stretched key of the code is saved, in `api-pairing.txt`, which only your user can read, and the command deletes the file when it ends, unless the file holds another code by then or was changed by another program. `ctw api-pair --list` shows the paired browsers and when they paired, and `ctw api-pair --remove <chrome|firefox>` unpairs one, whose token stops working at once. When `ctw watch` runs the local API, a pairing extension is shown the name `ctw watch <version> on <computer name>`.
- The app's Local API dialog pairs a browser extension too. Its "Browser extension" group shows when Chrome and Firefox paired (or "Not paired", or "Unknown" with a note when `api-clients.txt` can't be used), with an "Unpair" button for each, whose token stops working at once. "Pair extension...", which works while the API listens, shows an 8-symbol code in large type with the time left of its 5 minutes, then "Paired with the Chrome extension" (or Firefox) or why the code ended. There is no Copy button for the code. Closing the dialog, or the local API stopping, cancels a code that is still pending. When the app runs the local API, a pairing extension is shown the name `Chan Thread Watch <version> on <computer name>`. Moving the settings folder takes the paired browsers (`api-clients.txt`) along, owner-only, and ends a pending code (`api-pairing.txt` is deleted, not moved); backups never copy either file.
- When `ctw add` or `ctw remove` is changing the thread list as the app starts, the app waits up to about 10 seconds for it, then says that the command line is using the folder.

### Changed

- The Downloads window keeps each finished download listed for about 5 seconds, marked "Done" or "Failed" in the Progress column. A successful download shows its final size. Before, small files often finished between two updates of the list, so the list looked empty while the title showed a download speed. Downloads that fail before the transfer starts (for example a 404 or a connection error) are still not listed.
- Downloads use secure connections with TLS 1.3 when the server supports it. Before, the app asked for TLS 1.2.
- Redirects with status 308 (Permanent Redirect) are now followed, like the other redirects. Before, the download failed with "HTTP 308".
- The `HTTP_PROXY` and `HTTPS_PROXY` environment variables, when set, are now used before the Windows proxy settings.
- A redirect from an https address to an http address is no longer followed, so a download never falls back to an unencrypted connection. The download fails with the redirect's status, for example "HTTP 302 Found".
- The app keeps a file named `ctw.lock` in the settings folder while it runs. When the settings folder is shared (for example the app in portable mode on a network drive) and the app is already running on another computer, starting it shows a warning that names that computer, and you choose whether to start anyway. If both keep running, their saves can overwrite each other's thread list. Versions before 1.40.0 don't create the file, so a copy of an older version is not detected. If you start anyway, the app takes the lock once the other copy closes. The file stays after the app closes and can be ignored.
- The app no longer starts when it can't write to its settings folder (for example a read-only program folder in portable mode). It shows the folder and how to fix it. Before, it started, but its settings and thread list could not be saved.
- `threads.txt`, `threads.txt.bak` and `settings.txt` no longer keep their own permissions, hidden attribute or creation time when they are saved. They get the permissions of the settings folder, as a new file there does.
- A download or completed folder written as a Unix-style path (for example `/x`) is no longer read as `C:\x`. At startup the app logs it and uses the default folder for that session; the setting itself is kept. The settings window rejects such a folder with an error. A thread whose folder in the thread list starts with `/` no longer loads; it is logged, and the thread list is kept aside as for any thread that does not load.

### Fixed

- Saving the thread list (and its backup `threads.txt.bak`) or the settings should no longer leave `~RF*.TMP` files in the settings folder.
- Some folder names that contain `%` followed by two hex digits (for example `%41`, or `%2e%2e`, which was saved as the parent folder) were saved under the wrong name in the thread list, so after a restart the thread used a different folder.
- A thread folder on a network share (for example `\\server\share\...`) is now saved correctly in the thread list when the download folder is on a local drive, and a thread folder on a local drive when the download folder is on a share. Before, the saved folder was wrong, so after a restart the thread saved to a different folder or could not save its files.
- Moving a finished thread to the completed folder no longer deletes an empty thread folder that is on another drive or network share than the download folder.
- Threads already saved in the thread list with a wrong folder keep that folder.
- Saved thread pages link correctly to files and folders whose names contain `%`, and thread links also to names that contain `#` (image links already handled `#`). Before, such a link opened the wrong file or none.
- Spaces in the links of saved thread pages are now written as `%20`, so the offline page helper also shows images whose names contain spaces in the page.

### Security

- A thread address that includes a login, such as `http://name:password@host/...`, no longer leaks that login. Before, image and thumbnail requests to other servers carried it in the Referer header, and links in saved thread pages included it, so sharing a saved thread shared the password. Pages saved before this version keep the old links until the thread is saved again.
- The page size limit (32 MB) now applies to every thread page, whatever its type (for example a JSON page), and to the page a meta refresh leads to. Before, only HTML pages were limited, so a server could make the app hold an unlimited page in memory.

## v1.39.0 (2026-10-03)

This is the first release from the new maintainer (kevin-contino). It continues from SuperGouge's version 1.17.1. Version 1.17.1 cannot find this update by itself, so download it from the releases page.

### Before you upgrade

- The app needs .NET Framework 4.8. Windows 10 (version 1903 and later) and Windows 11 already include it. Windows 7 SP1, 8.1 and Windows 10 versions 1607 to 1809 can install it for free. Windows 10 versions 1507 and 1511 cannot run it, and Windows XP, Vista, Windows 7 without SP1 and Windows 8.0 are no longer supported. (#3)
- The download is `ChanThreadWatch.exe` plus `ChanThreadWatch.exe.config`. Keep both files in the same folder. Use `SHA256SUMS.txt` to check the files.
- This version encrypts saved logins the first time it saves. If you go back to 1.17.1 after that, you have to enter your logins again there. (#20)

### New

- Saved thread pages include an offline helper. Hover over a >> quote to preview the post, see backlinks under quoted posts, and click a thumbnail to expand the downloaded image in place (images only; videos open as before). It works on 4chan, 8chan-style boards, warosu, 4plebs, desuarchive and similar archives, and Endchan. It does not add backlinks where the archive already shows them, and image expansion needs "Save thumbnails" turned on. Pages saved earlier get the helper the next time the thread is saved or reparsed. (#30)
- Download problems show up in the thread list instead of failing silently, for example "Error: HTTP 403 Forbidden, waiting 60 seconds", "1 file failed, waiting 60 seconds" or "certificate not trusted for (host)". The app retries failed files on the next checks. A one-time download that fails stops with the error instead of saying "Download complete", and a failed reparse adds ", reparse failed: (reason)". (#16, #21, #27)
- When a site says you are downloading too fast (HTTP 429, or 503 with a wait time), the app pauses every request to that server and shows "Rate limited by (host) until HH:mm:ss". The pause uses the site's wait time, kept between 5 seconds and 2 hours. If the site gives no wait time, the pause starts at 60 seconds and doubles each time. Downloads resume by themselves afterwards. (#29)
- The update check works again and looks at this project's releases. The About box also credits the previous maintainer. (#2, #10)

### Changed

- 4chan threads download again. 4chan blocks requests that don't look like a web browser, so the app now identifies itself as one by default. Your "Custom user agent" setting still wins. (#1)
- Site list: Endchan moved to endchan.net and endchan.org (replacing endchan.xyz), and archive.alice.al and arch.b4k.dev were added. Krautchan, nyafuu, Fireden, loveisover, b-stats and 4chanarchives no longer have site-specific support. (#5)
- The app opens one connection per server at a time (it was 4) and waits at least one second between requests. Threads with many files download more slowly, about one file per second per server, but this avoids rate limits. (#29)
- A server that sends data extremely slowly now times out and the download is retried. Pages time out after 5 minutes. Files time out when they drop below about 10 KB per second after the first 5 minutes. Time spent under your own speed limit does not count. (#15, #21, #27)
- New size limits: the app rejects pages over 32 MB, and it skips files over 512 MB and reports them as failed. (#15, #16)
- Auto-follow adds at most 100 linked threads for each watched thread. (#16)
- The thumbnails setting is now called "Save thumbnails and rewrite links to local files". (#28)
- Saved pages are always written as UTF-8 with a byte order mark, whatever encoding the site used. (#31)
- Threads on servers reached by IP address or by a one-word name (such as localhost) get proper folder names. Existing threads on those servers keep their folders but get a new internal ID, so a blacklist entry for one of them stops matching. (#21, #27)
- Logins go only to the exact address of the thread page: same http or https, server name and port. That applies to images, redirects and auto-followed threads. If a site serves login-protected images from another server or subdomain, those images may now fail with 401. (#9)

### Fixed

- The image login checkbox in the thread edit window stayed unchecked and could clear a thread's image login on save. (#10)
- With a speed limit set, downloads got slower the longer the app ran. That is fixed, closing a download twice no longer crashes, and a speed-limited download stops right away. (#10, #15)
- If moving the settings folder failed, a second copy of the app could open the same settings. (#10)
- On a first run, the app created the log file but wrote nothing to it for that session. (#10)
- The thread list and settings are harder to lose. Saves are atomic, an unreadable file is copied aside before anything overwrites it, and one bad entry no longer stops the rest from loading. A thread set as its own parent no longer crashes the app, and line breaks in a description can no longer corrupt the thread list. (#13)
- An error, ban or Cloudflare page no longer replaces the saved copy of a thread. (#16)
- Unusual pages from 4chan, 8chan, warosu, FoolFuuka archives and Endchan, including pages with missing images or dead links, caused crashes and wrong results. Links in saved pages work again: links to threads the app does not follow point to the live page, and files that could not be saved keep their online link. An 8chan thread no longer follows links to itself. (#12, #16, #21)
- Images are requested with the thread page as Referer. Before, the app only sent it when a login was set, so sites that check it could refuse images. (#9, #12)
- Restored deleted posts keep their original order, even when more than 16 in a row were deleted. (#19)
- warosu: reply images download now (before, only the first post's image did), and image checksums containing "/" or "+" are read correctly. desuarchive: saved pages no longer link to online copies of images that were downloaded. (#25, #32)
- With thumbnails off, changing the setting while a thread was being checked stopped the thread. (#28)
- Unexpected errors go to the log. In the main window they show one message box; errors in background work are logged before the app closes. Stopping a thread no longer waits for a slow DNS lookup. (#15, #27)

### Security

- HTTPS certificate checks are back on. They were off for every connection, including ones that send logins. Sites with an expired or self-signed certificate now fail with "certificate not trusted". (#9)
- Saved logins are encrypted on your PC with Windows DPAPI. Old plain-text logins are converted the next time the app saves. Encrypted logins can't be read on another PC or Windows account, so you have to enter them again there. (#20)
- Backup and recovery copies no longer hold plain-text logins. `threads.txt.bak` is encrypted on the first save, and logins are blanked in recovery copies (`threads.txt.corrupt-...`, `settings.txt.corrupt-...`). In a damaged copy, a nearby field that isn't a login may be blanked too. Copies over 64 MB are left as they are. (#27, #33)
- Saved pages contain no scripts, embedded frames or objects, event handlers or javascript: links, whether or not thumbnails are saved. Each page also carries a policy that blocks anything the cleanup missed. Pages saved earlier with thumbnails off are cleaned the next time the full thread page downloads. (#17, #28, #31)
- File and folder names built from poster names or file names can no longer escape the download folder or use reserved Windows names such as CON or NUL. (#9)
- The update check ignores release tags that are not plain version numbers. (#10)
