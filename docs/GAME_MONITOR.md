# Game Monitor: what exists and what has actually been verified

Status as of NetPulse 0.14.0 (2026-10-18).

## Support status of the seven games

| Game | Status | Process names verified on a real install | Official status page |
|---|---|---|---|
| League of Legends | **Reference-only** | Yes (this development PC) | https://status.riotgames.com/ |
| Fortnite | Reference-only | No (documented knowledge) | https://status.epicgames.com/ |
| Apex Legends | Reference-only | No | https://help.ea.com/en/server-status/ |
| Counter-Strike 2 | Reference-only | No | none official (Valve publishes none) |
| World of Warcraft | Reference-only | No | none verified |
| Marvel Rivals | Reference-only | No (least certain) | none found |
| Path of Exile | Reference-only | No | none verified |

*Reference-only* means: the process can be detected, its sockets can be listed, and generic reference measurements work, but
**actual game-server monitoring is not available or not validated**. `Supported` is derived in code from
`ValidationLevel.LiveValidated` (see `GameProfile.Status`); a test fails if any profile is Supported without it.
No profile lists a "verified endpoint": match servers are dynamic and none was verified.

## What the monitor does

- **Process detection** (`GameProcessDetector`, `GameWatcher`): rules per profile with roles Game / Launcher / AntiCheat / Helper /
  CrashReporter. Only a Game-role process means "running". Launcher-only is reported as such. A same-named process in the wrong
  install folder is reported *Uncertain* and not claimed. Instances are tracked by (PID, start time), so restarts and recycled PIDs
  are handled. Polling: 2 s while monitoring, 5 s while a game is selected but idle; nothing when no game is selected.
- **Connection discovery** (`WindowsConnectionSource`): `GetExtendedTcpTable` / `GetExtendedUdpTable`, IPv4 and IPv6, per owning PID,
  no administrator rights, no `netstat` parsing. Records local/remote endpoint, protocol, PID, state, first/last seen; deduplicates;
  reports appeared / state-changed / disappeared (a connection must be missing for two polls first).
- **Endpoint role** is always *Unknown*. Nothing observable is reliable evidence that an address is the match server.
  A separate, clearly worded hint ("What it probably is", `ConnectionKinds`) follows protocol, scope and port convention only:
  TCP to public port 443/80/8443 is a web/API service, UDP sockets carry the (hidden) match traffic, loopback stays on the PC.
  Repeated reconnects to the same endpoint are grouped into one row with a count.
- **Real League recording (2026-10-10, 36 min):** the game process was detected for the whole match; its 35 sockets were
  30 HTTPS connections (Cloudflare and AWS, reconnecting every two minutes), 4 loopback sockets (including the local game
  API on port 2999) and one UDP socket open all match. The match server's address was therefore never visible, which the
  UI now states while a game runs with UDP sockets.
- **Measurement** of endpoints you choose, through the shared measurement engine: ICMP, or TCP connect for TCP endpoints. The app refuses
  methods that are not meaningful (UDP socket with no visible peer, loopback, LAN, multicast) and explains why. An endpoint that never
  answers is "not responding to this method (may filter probes)", never "offline". Game endpoints are excluded from the general
  connection diagnosis and from the Internet-disconnect detection.
- **Findings** (`GameDiagnosticAnalyzer`): each has observations (facts), possible explanations, evidence, contradictions, a confidence
  and a next test. Latency is judged against each target's own baseline, never a fixed ping. Contradicting measurements lower confidence.
- **Sessions**: samples, events, monitoring gaps (kept separate from outages), disconnects, lag markers, game processes, connections,
  limitations and final findings, saved locally under `%LOCALAPPDATA%\NetPulse\sessions`. Checkpointed every 30 s and saved on shutdown.
  Each recording stores the NetPulse version that made it, because its findings are that version's analysis.
- **Mark lag shortcut** (`GlobalHotkey`, Windows `RegisterHotKey`): a system-wide shortcut, `Ctrl+Shift+F9` by default, marks lag in
  whichever recording runs, also while the game has focus. Optional confirmation sound (a different sound when nothing is recording).
  If another program owns the combination, Settings says so instead of failing silently.
- **Automatic recording** (off by default): when the selected game starts, recording starts; once the game has stayed closed for 20 s
  the recording is stopped and saved, so a quick restart does not split it. A recording started by hand is never stopped automatically,
  and a running network recording is never interrupted.

## Known limitations (also shown in the UI)

1. **UDP remote peers are invisible.** Windows reports only the local port of a UDP socket. Most real-time game traffic is UDP, so the
   remote endpoints listed are mostly TCP (login, matchmaking, API...). Seeing UDP peers needs elevated packet tracing (ETW), which is
   not implemented.
2. Polling can miss connections that open and close between polls.
3. In-game latency is never measured. Reference measurements describe the connection in general.
4. Process names of six games are unverified; detection may fail for them.

## Verification performed

**Automated (340 tests, all passing):** profile invariants; process detection with simulated processes (multiple processes, launchers,
anti-cheat, path mismatch, restarts, PID reuse); connection tracking with synthetic IPv4/IPv6/TCP/UDP snapshots (dedup, state change,
flapping, short-lived, owner exit); the **real Windows APIs** against sockets created by the test process itself (IPv4 and IPv6 TCP and
UDP); endpoint advice and ICMP-filtering handling; analyzer scenarios incl. contradictions and confidence; the full monitor workflow
with fake sources (start, game launch, measure, mark lag, exit, stop, saved file) and shutdown while monitoring; engine behaviour for
game endpoints; settings, sessions, cancellation; automatic recording (start, grace period, restart, never stopping a manual
recording); Mark lag routing between the Game Monitor and network recordings; shortcut parsing.

**Real League of Legends client (read-only, development PC):** with the Riot Client running and no match, the app correctly reported
"Only League of Legends support programs (launcher, crash reporter) are running; the game itself is not", Start/Mark lag/Stop & save
worked and produced a saved session. Installed executable names were read from the actual install folder.

**Published build (`build.ps1`):** self-test inside the published exe (real process list, real socket tables, real session file, settings
round-trip) and a UI Automation run of the published exe against an isolated data folder: Game Monitor workflow, Mark lag by
button and by the real system-wide shortcut, opening the saved recording in History, and a settings change.

## NOT verified (needs a live League match)

- Detection of `League of Legends.exe` while a match is running, and its PID/path rules.
- The set of connections a real match produces (and that the TCP ones are visible while UDP ones are not).
- ICMP/TCP measurement of a real match-related endpoint, and how the findings behave during real lag.
- Any behaviour for the other six games.

Live validation checklist (needs your approval; do not start a match for NetPulse): run NetPulse, choose League of Legends, press
Start monitoring *before* launching a match, play, press Mark lag when it stutters, Stop & save, then review the session file's
`gameProcesses`, `connections` and `events`. Only when that matches reality should `ValidationLevel.LiveValidated` be set for the
League profile.
