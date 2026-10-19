# Security

NetPulse is a local diagnostics tool. This page says what it can do on your PC, what it sends, what it keeps, and how to report a problem.

## What NetPulse never does

- It never asks for administrator rights. It runs as you, and Windows limits it to what you can do yourself.
- It never captures or reads your traffic. It sees which programs have connections open (as Task Manager does), not what they send.
- It has no telemetry, no analytics, no account and no automatic updates. Nothing is uploaded, ever: not settings, not recordings, not reports.
- It never changes a setting of Windows, your firewall or your router. The Security check only reads.
- It never solves a CAPTCHA or bot check, and never uses an API that requires an account or key.

## What NetPulse sends, and when

| When | To | What |
|---|---|---|
| While running | Your router and the reference servers in Settings (by default 1.1.1.1, 8.8.8.8, 9.9.9.9 and cloudflare.com) | A ping, a DNS lookup and an HTTPS handshake about once a second |
| When you run Speed test or Bufferbloat | The test provider (Cloudflare's speed test) | Downloads and uploads of test data |
| When you press Check on Site check (or switch on its regular check) | Each listed site; Cloudflare and Google public DNS over HTTPS; the official status pages of Reddit, Twitch, Discord, Wikipedia and Epic Games | One name lookup, one connection and one `GET /` per site, without cookies; the site's icon once a month |
| When you press Check on Security check | Cloudflare public DNS; www.google.com, www.cloudflare.com, www.wikipedia.org; your router | One made-up name lookup; a TLS handshake (nothing sent over it); one UPnP search on the local network |
| When you open a game's status page | The page, in your browser | Only on your click |

## What NetPulse keeps

Everything stays in `%LOCALAPPDATA%\NetPulse` (your user profile, readable only by you and administrators): settings, recordings, test results (`test-history.json`), the connection diary (`diary.json`), usual pings (`baselines.json`), site and game icons, and an error log. Delete the folder to remove all of it.

Exported reports leave out addresses on your home network and IPv6 addresses (they read `[hidden]`) unless you turn on "Include home-network and IPv6 addresses in exported reports" in Settings > Privacy and data. Copy summary never includes addresses or network names. The Wi-Fi network name and the access point's address are never put in a report.

## How it is built to be safe

- Windows tools (netsh, Explorer, cmd) are started by their full path in the Windows folder, never by name, so a program with the same name placed next to NetPulse cannot run instead.
- Only `http` and `https` addresses are ever handed to the browser; folders are opened in Explorer by full path.
- Data from the internet is treated as untrusted: answers are read with fixed size limits, icons are limited to 256 KB and 512 pixels and only ICO, PNG, GIF and JPEG are decoded, and file names come from validated host names.
- Settings and other files are written to a temporary file and then moved into place, so a crash cannot leave half a file; an unreadable file is set aside, not overwritten.
- The released program has no third-party libraries: only .NET and Windows.
- The uninstaller deletes only the three files it installed and an empty folder, and refuses a folder name with characters that could change its command line.

## Check your download

Each release lists the SHA-256 checksum of its zip. Before running a copy you got from anywhere else, compare it:

```powershell
Get-FileHash .\NetPulse-0.16.0-Portable-win-x64.zip -Algorithm SHA256
```

The program is not code-signed, so Windows SmartScreen may warn the first time you start it.

## Reporting a vulnerability

Please report security problems privately through GitHub's "Report a vulnerability" (Security tab of the repository) rather than in a public issue. Say what you found, how to reproduce it and which version you used. You will get an answer, and a fix will be released as a new version with a note in the release notes.
