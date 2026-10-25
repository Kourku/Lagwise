# Microsoft Store: listing text and submission notes

Everything to paste into Partner Center, written to match what Lagwise really does (see [../../README.md](../../README.md) and [../../SECURITY.md](../../SECURITY.md)). Lagwise never claims game-server ping; the text does not either.

## Product identity (from Partner Center)

Fill `installer/store/identity.json` with the three values from *Product management > Product identity*, then run:

```powershell
.\scripts\Build-Msix.ps1
```

The package `dist\store\Lagwise-<version>-x64.msix` is unsigned on purpose; the Store signs it.

## Properties

| Field | Value |
|---|---|
| Category | Utilities & tools > Networking (or Games > Game tools if offered) |
| Price | Free, or a one-time price (the app is identical; see the monetization notes) |
| Privacy policy URL | `https://kourku.github.io/Lagwise/PRIVACY` once GitHub Pages is on, otherwise `https://github.com/Kourku/Lagwise/blob/main/docs/PRIVACY.md` |
| Website | `https://github.com/Kourku/Lagwise` |
| Support contact | `https://github.com/Kourku/Lagwise/issues` |
| Age rating (IARC) | Utility: no violence, no user-generated content, no sharing of location, no purchases inside the app, no web browsing. Expected rating: 3+ / Everyone. |
| Hardware | Windows 10 1809 or later, 64-bit (x64) |

## Short description (up to 270 characters)

Was it my connection? Lagwise records your internet while you play any game and tells you afterwards whether lag came from your Wi-Fi or router, your provider, this PC or further away. Free of accounts and tracking: everything stays on your PC.

## Description

Lagwise answers one question: was that lag my connection?

Most ping tools show one number. Lagwise watches every step between your PC and the internet at once (your adapter, your router, your provider and the wider internet) and tells you which step is the problem, in plain words.

**Record a game, get a verdict**
Start a recording (or let Lagwise notice a game starting), play, and press a hotkey when it lags. Afterwards Lagwise tells you whether those moments came from your Wi-Fi or router, from beyond your router, or from this PC itself, and what to try.

**Works with any game**
Lagwise picks up the games Windows already knows about. It measures your connection to the internet, not any one game's servers, and says so.

**Everything in one place**
- Live view of your connection with a mini overlay for in front of your game
- Speed test and a "lag under load" (bufferbloat) test
- Hop-by-hop route trace to see where delay starts
- Site check: is it just you, or is the site down?
- Security check of your PC and connection (read-only)
- Wi-Fi details: signal, channel, link rate, and whether a repeater or mesh may be the weak link
- Readiness check, connection diary and history of your recordings, exportable as a report

**Private by design**
No account. No telemetry. No ads. Your settings and recordings are saved on your PC and nowhere else. Lagwise only contacts your router and the public servers you can see in Settings, and the speed test service when you run a speed test.

**Made for Windows**
Taskbar jump list and badge, tray icon with a status dot, sleep and wake handled, crisp on every monitor, snap layouts, and an optional start with Windows.

Lagwise is open source (MIT): https://github.com/Kourku/Lagwise

## What's new in this version

First Microsoft Store release of Lagwise 1.0 (formerly NetPulse): Windows integration, fewer false alarms about your router, and a clearer "was it my connection?" verdict.

## Key features (up to 20 bullets)

- Records your connection while you play any game
- Tells you if lag came from your Wi-Fi or router, your provider, this PC or beyond
- Mark lag moments with a hotkey while you play
- Live mini overlay in front of your game
- Speed test and bufferbloat (lag under load) test
- Route trace to see where delay starts
- Site check: is it you or the site?
- Read-only security check of your PC and connection
- Wi-Fi details and repeater or mesh hints
- No account, no telemetry, no ads: everything stays on your PC

## Search keywords (up to 7)

`ping`, `lag`, `network`, `wifi`, `latency`, `gaming`, `internet speed`

## Certification notes (for the Microsoft reviewer)

- Lagwise is a full-trust desktop app (`runFullTrust`), because it needs raw network measurement (ICMP echo, TCP and DNS timing), Windows' Wi-Fi API and the list of running programs. It never asks for administrator rights.
- No sign-in is needed. Open the app and the Overview page starts measuring at once. To see a recording: Game Monitor > Start recording, wait a minute, Stop. Speed test needs internet access (Cloudflare's public speed test).
- It makes no connections to the publisher. Every destination is listed in SECURITY.md in the repository.
- "Start with Windows" in Settings uses the package's startup task and is off by default.

## Screenshots (at least one; 1366 x 768 or larger, PNG)

Take them with `Lagwise.exe --screenshot "Overview=o.png;GameMonitor=gm.png" --screenshot-window` (see docs/VERIFICATION.md; run it with a separate `LAGWISE_DATA_DIR` so your own data is not shown), for: Overview, Game Monitor with a verdict, History, Adapter with Wi-Fi details, Speed test. Do not show your own Wi-Fi name or addresses. The 300 x 300 store icon is `installer/store/listing/store-icon-300.png`.

## Account steps only you can do

1. Partner Center > Apps and games > New product > MSIX or PWA app > reserve the name **Lagwise** (if taken, try "Lagwise: Was it my connection?").
2. Copy the three identity values into `installer/store/identity.json`.
3. Create the submission: pricing and availability, properties and age rating (IARC questionnaire), the text above, screenshots, the privacy policy URL, and the `.msix` package.
4. Submit for certification (usually a few days).
