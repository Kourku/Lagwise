# Security

Lagwise is a local diagnostics tool. This page says what it can do on your PC, what it sends, what it keeps, and how to report a problem.

## Does running Lagwise put you at risk?

This section is about Lagwise itself: what someone could do to you through it, and what we checked.

**Nothing on the network can reach Lagwise.** It never listens for connections: a running Lagwise has no open TCP port and no lasting UDP socket (checked with `Get-NetTCPConnection` and `Get-NetUDPEndpoint` on its process), and it creates no Windows Firewall rule, so the "allow this app" prompt never appears. It only ever answers its own questions, and since 1.0 a DNS answer counts only when it comes from the server asked and carries the question's number.

**Starting Lagwise again only talks to the Lagwise already running, on this PC.** Since 1.0 a second start (a double-click, the taskbar jump list, the installer) hands its request to the running Lagwise over a named pipe and closes. The pipe is owned by you and open only to you; Windows refuses it to every other computer (checked: a connection through `\\<this PC>\pipe\...`, the way file sharing would reach it, is refused); and only eight fixed words are accepted (show, mark lag, start or stop a recording, overlay, speed test, bufferbloat test, History, exit). Anything else is dropped unread.

**Its own runtime is kept current.** Lagwise carries its own .NET runtime, and Windows Update does not patch that copy. Releases up to 0.16.0 bundled .NET 10.0.3, which nine later security updates fixed. Since 1.0 the build asks Microsoft's release feed for the latest .NET 10 patch, bundles it, and refuses to finish if the exe reports anything else. The runtime in a release is named in its notes. **Use the newest Lagwise**: an old copy keeps the runtime it was built with.

**What comes from the internet is treated as hostile:**
- Text from outside (reverse-DNS names of route hops, certificate names, status pages, redirect addresses, server messages) has control and invisible characters removed and its length limited before it is shown or put in a report. Reverse-DNS names that are not valid host names are dropped. Those characters can make text read differently than it is.
- CSV exports quote every text field, and a field starting with `=`, `+`, `-` or `@` gets an apostrophe, so a spreadsheet never runs it as a formula.
- Site icons: a page or a redirect chooses the icon's address, so Lagwise refuses addresses on this PC or your home network. A website cannot use Lagwise to send requests to your router. Icons are limited to 256 KB and 512 pixels, and only ICO, PNG, GIF and JPEG are decoded.
- Answers are read with size and time limits: 1 MB for status and DNS-over-HTTPS answers, 48 KB of a home page, and a time limit on reading a page for its icon, so a page built to be slow cannot stall it.
- Nothing received is ever run, opened as a file, or used to build a command line. Only `http` and `https` links from Lagwise's own list are given to the browser.

**It cannot be used to gain rights.** It runs as you, never asks for administrator rights, and the installer is per-user (your profile and `HKEY_CURRENT_USER` only). Windows tools are started by full path, so a fake netsh.exe or cmd.exe next to it does not run. The single-file exe unpacks its native parts to your own temporary folder, which only you can write to.

**What others can see:** your router and the reference servers receive a ping, a DNS lookup and an HTTPS handshake about once a second. The sites you check see a request with the user agent `Lagwise/<version>`. Devices on your network see one UPnP search when you run the Security check. None of it carries anything about you. Exported reports keep public route hops (your provider's routers), which can show roughly where you are.

**What is left:** the program is not code-signed, so check the SHA-256 of a download (below). Images from the internet go through Windows' own image decoders. Those are patched by Windows Update, and Lagwise only gives them small images in four formats.

## What Lagwise never does

- It never asks for administrator rights. It runs as you, and Windows limits it to what you can do yourself.
- It never captures or reads your traffic. It sees which programs have connections open (as Task Manager does), not what they send.
- It has no telemetry, no analytics, no account and no automatic updates. Nothing is uploaded, ever: not settings, not recordings, not reports.
- It never changes a setting of Windows, your firewall or your router. The Security check only reads. The one entry Lagwise writes outside its own folder is its startup entry (`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, value `Lagwise`), when you switch "Start Lagwise when I sign in to Windows" on or off; switching it on there also lifts an "off" you set for Lagwise in Task Manager. A portable copy that was moved points its own existing entry at its new place. Uninstalling removes the entry.
- It never solves a CAPTCHA or bot check, and never uses an API that requires an account or key.

## What Lagwise sends, and when

| When | To | What |
|---|---|---|
| While running | Your router and the reference servers in Settings (by default 1.1.1.1, 8.8.8.8, 9.9.9.9 and cloudflare.com) | A ping, a DNS lookup and an HTTPS handshake about once a second |
| When you run Speed test or Bufferbloat | The test provider (Cloudflare's speed test) | Downloads and uploads of test data |
| When you press Check on Site check (or switch on its regular check) | Each listed site; Cloudflare and Google public DNS over HTTPS; the official status pages of Reddit, Twitch, Discord, Wikipedia and Epic Games | One name lookup, one connection and one `GET /` per site, without cookies; the site's icon once a month |
| When you press Check on Security check | Cloudflare public DNS; www.google.com, www.cloudflare.com, www.wikipedia.org; your router | One made-up name lookup; a TLS handshake (nothing sent over it); one UPnP search on the local network |
| When you trace a route (Route trace) | The address you trace, and your own DNS | Pings with rising time-to-live along the path; a reverse-DNS lookup of each hop's address. Network names of hops come from a list built into Lagwise, not from an online service |
| When you open a game's status page | The page, in your browser | Only on your click |

## What Lagwise reads on this PC

All read-only, as you, without administrator rights:

- **Windows' list of your games** (`HKEY_CURRENT_USER\System\GameConfigStore`, the list Game Bar and Game Mode keep): which programs are games and when they last ran. Lagwise also reads the product name of those programs and takes their icon.
- **The list of running programs**, to notice a game starting; the location of a program is only read when its name belongs to a known game.
- **The game's own network sockets while it is recorded** (as Task Manager shows them): whether it is online. Not its traffic.
- **Processor load, network adapter counters and the Wi-Fi details Windows reports**, from Windows' WLAN API (with `netsh wlan show interfaces` as a fallback). Since Windows 11 24H2 Windows shares those only with apps allowed to use location; Lagwise then says so, but never asks Windows where you are.
- **Which adapter Windows routes internet traffic through** (a lookup in its routing table; nothing is sent), and whether that is a VPN.
- **Its own startup entry, and Task Manager's on/off switch for it.**

## What Lagwise keeps

Everything stays in `%LOCALAPPDATA%\Lagwise` (your user profile, readable only by you and administrators): settings, recordings, test results (`test-history.json`), the connection diary (`diary.json`), usual pings (`baselines.json`), site and game icons, and an error log. Delete the folder to remove all of it.

Exported reports leave out addresses on your home network and IPv6 addresses (they read `[hidden]`) unless you turn on "Include home-network and IPv6 addresses in exported reports" in Settings > Privacy and data. Copy summary never includes addresses or network names. The Wi-Fi network name and the access point's address are never put in a report.

## How it is built to be safe

- Windows tools (netsh, Explorer, cmd) are started by their full path in the Windows folder, never by name, so a program with the same name placed next to Lagwise cannot run instead.
- Only `http` and `https` addresses are ever handed to the browser; folders are opened in Explorer by full path.
- Data from the internet is treated as untrusted (see above). File names come from validated host names.
- The release build bundles the latest .NET 10 runtime patch and checks the published exe runs on it.
- Settings and other files are written to a temporary file and then moved into place, so a crash cannot leave half a file; an unreadable file is set aside, not overwritten.
- The released program has no third-party libraries: only .NET and Windows.
- The uninstaller deletes only the three files it installed and an empty folder, and refuses a folder name with characters that could change its command line.

## Check your download

Each release lists the SHA-256 checksum of its zip. Before running a copy you got from anywhere else, compare it:

```powershell
Get-FileHash .\Lagwise-1.0.0-Portable-win-x64.zip -Algorithm SHA256
```

The program is not code-signed, so Windows SmartScreen may warn the first time you start it.

## Reporting a vulnerability

Please report security problems privately through GitHub's "Report a vulnerability" (Security tab of the repository) rather than in a public issue. Say what you found, how to reproduce it and which version you used. You will get an answer, and a fix will be released as a new version with a note in the release notes.
