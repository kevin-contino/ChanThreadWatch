# Changelog

User-visible changes to Chan Thread Watch. Each merged change that users can see, or that changes how the app behaves, raises the minor version by one.

## v1.40.0 (unreleased)

### Security

- A thread address that includes a login, such as `http://name:password@host/...`, no longer leaks that login. Before, image and thumbnail requests to other servers carried it in the Referer header, and links in saved thread pages included it, so sharing a saved thread shared the password. Pages saved before this version keep the old links until the thread is saved again.

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
