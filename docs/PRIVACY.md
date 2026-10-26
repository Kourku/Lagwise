# Lagwise privacy policy

*Applies to Lagwise 1.0 and later, in every edition (downloaded, installed from the app, or from the Microsoft Store). Last updated 27 October 2026.*

**Short version: Lagwise does not collect any information about you. Nothing it measures, records or saves leaves your PC.**

## What Lagwise collects

Nothing. Lagwise has no account, no sign-in, no telemetry, no analytics, no advertising and no crash reporting that sends anything anywhere. The people who make Lagwise cannot see who uses it, how often, or what it measured.

## What stays on your PC

Settings, recordings, test results, the connection diary, the "usual" ping values, site and game icons and an error log are saved in `%LOCALAPPDATA%\Lagwise` in your own Windows profile. They are only ever read by Lagwise and by you. Reports you export are files you save yourself and share (or not) as you choose; by default they leave out addresses on your home network and IPv6 addresses. Removing the Microsoft Store edition removes its data; for the other editions, delete the folder.

## What Lagwise sends over the internet

Only what is needed to measure your connection, and only to the servers listed here, never to the makers of Lagwise:

- While it is running: small test packets to your router and to the reference servers you see in Settings (by default 1.1.1.1, 8.8.8.8, 9.9.9.9 and cloudflare.com).
- When you press Speed test or Bufferbloat: test downloads and uploads to Cloudflare's public speed test service.
- When you press Check on Site check or Security check, or run a route trace: the sites, public DNS services and addresses you chose or that the page names.

Those servers see your public IP address, as every server you connect to does. Their own privacy policies apply to what they do with it (for example [Cloudflare's](https://www.cloudflare.com/privacypolicy/) and [Google Public DNS's](https://developers.google.com/speed/public-dns/privacy)). The full list, with what is sent to each, is in [SECURITY.md](https://github.com/Kourku/Lagwise/blob/main/SECURITY.md#what-lagwise-sends-and-when).

## What Lagwise reads on your PC

Read-only, to explain your connection: which network adapter is in use, Wi-Fi details Windows reports, which programs are games (from Windows' own game list) and whether a game is running and online. It never reads or captures what your programs send, and never asks Windows where you are.

## Children

Lagwise collects no personal information from anyone, including children.

## Changes

If this ever changes, the new policy will be published here and in the release notes before the version that changes it. The promise that Lagwise does not collect data is a design rule of the project.

## Contact

Questions about privacy: open an issue at <https://github.com/Kourku/Lagwise/issues>. To report a security problem privately, use GitHub's "Report a vulnerability" in the Security tab of the same repository.
