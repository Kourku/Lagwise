# Game Monitor: what it does, and how it is verified

Status as of NetPulse 1.0.0 (2026-10-20).

![The Game Monitor with the verdict of the last game session](screenshots/game-monitor-1.0.png)

## In one paragraph

While you play any of your games, NetPulse records how your side of the connection behaves: this PC, your Wi-Fi or
cable and router, and your provider, measured against your router and well-known internet servers, together with this
PC's own traffic and processor load. Afterwards it answers one question for the whole session and for every moment you
marked as laggy: **was it my connection?** It does this the same way for every game, so it works for any game on any PC,
and every part of it can be verified without a real match.

## What changed in 1.0, and why

Up to 0.17 the Game Monitor had per-game profiles for seven games, each labelled *Reference-only* until it was validated
in a real match. It listed the connections the game opened and let you measure one of them. In practice those were
the game's web services (logins, chat, telemetry). The match itself goes over UDP, and Windows hides where UDP traffic goes from programs
without administrator rights. Measuring a web service said nothing about in-game lag, and the live validation each game
needed could never be automated.

1.0 removes what could not be verified and builds on what can:

| Removed | Why |
|---|---|
| The connection table and "measure this endpoint" | It measured web services, not the match, and invited the wrong conclusion. |
| Per-game support labels and limitation lists | Nothing game-specific is claimed any more, so there is nothing to label. |
| The findings analyzer (confidence levels per finding) | Replaced by one verdict per session that a person can act on. |
| Shared store clients (Steam, Epic Games Launcher, EA app, Battle.net) as a game's "launcher" | They run whether or not a given game is played. |

| New | |
|---|---|
| **Any game** | Found in Windows' own game list, the built-in names, or added by you. Default: watch all of them. |
| **Automatic recording, on by default** | Starts when a game starts, saves when it has been closed for 20 seconds. |
| **The session verdict** | Was it my connection, and which part: home network, provider, this PC? Shown live while playing, after the session, and in History for every recording, including old ones. |
| **Processor and Wi-Fi signal** in recordings | A PC working flat out stutters like lag; a weak signal explains Wi-Fi trouble. |
| **Recent game sessions** | The last six, with their verdicts. |

## How games are found (on any PC)

Nothing depends on a particular PC's folders. Every source is read on the PC NetPulse runs on:

1. **Windows' own game list.** Windows 10 and 11 keep a list of the games they have seen run, for each user, under
   `HKEY_CURRENT_USER\System\GameConfigStore\Children`. Game Bar and Game Mode use it. Entries Windows matched to a
   program carry its full path (`MatchedExeFullPath`) and when it last ran (`LastAccessed`). NetPulse reads it, read-only,
   and leaves out programs that no longer exist. Programs Windows files under one title (a game and its own launcher) are one
   game. Names come from the program's product name, or from the game's folder, climbing past engine folders such as
   `Binaries\Win64` (for example `Arc Raiders\PioneerGame\Binaries\Win64\PioneerGame.exe` is "Arc Raiders").
2. **Built-in names** for seven games (below). They add a proper title, an official status page, and they tell a
   game's own client from the game: League's client is open in the lobby, but only `League of Legends.exe` is a match.
3. **Added by you:** "Add a game..." takes any program. A game can also be taken off the list ("Not a game?").

A game from Windows' list or added by you is recognised only by its **exact file**. Another program with the same
name somewhere else is a different program; one whose location Windows will not tell is not claimed. If Windows' list is
empty or missing (Game Bar disabled by policy, say), the built-in names and added games still work.

Verified on the development PC: Windows' list held 84 programs; League of Legends, Fortnite, Apex Legends
(`r5apex_dx12.exe`), Counter-Strike 2 and Marvel Rivals appeared with their real paths, confirming those built-in names.
World of Warcraft and Path of Exile were not installed: their built-in names come from documentation, and if they are wrong
the game is still found through Windows' list.

| Built-in game | Game program(s) | Client / launcher known | Official status page |
|---|---|---|---|
| League of Legends | `League of Legends.exe` (in a "League of Legends" folder) | LeagueClient, LeagueClientUx | status.riotgames.com |
| Fortnite | `FortniteClient-Win64-Shipping.exe` | FortniteLauncher | status.epicgames.com |
| Apex Legends | `r5apex_dx12.exe`, `r5apex.exe` | none | help.ea.com/en/server-status |
| Counter-Strike 2 | `cs2.exe` | none | none (Valve publishes none) |
| World of Warcraft | `Wow.exe`, `WowClassic.exe` (in a "World of Warcraft" folder) | none | none verified |
| Marvel Rivals | `Marvel-Win64-Shipping.exe` | none | none found |
| Path of Exile | `PathOfExile*.exe` | none | none verified |

## Recording

- **Watch for:** "All my games" (default) or one game. Process detection polls every 5 seconds while watching and every 2
  seconds while recording. Process paths are only read for programs whose name belongs to a known game.
- **Automatic recording** (on by default): a game starting starts a recording, unless another recording already runs.
  It is saved once the game has stayed closed for 20 seconds, so a quick restart or a loading screen does not split it. A
  recording started by hand is never stopped automatically, and a recording you stop while the game runs is not
  restarted until the game has closed and started again. **Record now** with all games watched and none running makes
  a recording that takes the first game that starts.
- **Mark lag:** the button, or a system-wide shortcut (`Ctrl+Shift+F9` by default) that works while the game has focus,
  with an optional confirmation sound.
- **What a recording keeps:** every probe to your router and the reference servers, every second of this PC's traffic,
  processor load and Wi-Fi signal, events, monitoring gaps (kept apart from outages), connection drops, lag marks, the game's
  processes and its sockets (whether it was online, and over UDP). The match server's address is not among them, because
  Windows does not reveal it.
- **Keeping it tidy:** the newest 50 automatic recordings are kept (Settings > Recordings); older ones go when a new one
  is saved, except recordings in which you marked lag. Interrupted recordings are always kept.

## The verdict: was it my connection?

Worked out from the recording itself (see [METHODOLOGY.md](METHODOLOGY.md#9-session-verdict)), so every recording gets
one in History, old ones included, and the live view shows it for the session so far.

- The recording is judged in **5-second slots**. Each server is compared with **its own norm in that recording**:
  - the router counts as slow at 30 ms above its usual (or three times its usual);
  - an internet server counts as slow at 50 ms above its usual (or 1.5 times).
  - A server that is unsteady in more than 30% of the slots is noisy by nature (rate-limited pings) and is left out.
- **Cause of a slot:**
  1. nothing answered: **the connection dropped**;
  2. the router itself was slow: **your Wi-Fi or router** (home network);
  3. most internet servers were slow at once while the router was fine: **beyond your router**, at your provider or the
     internet; if this PC was downloading or uploading heavily at the same time, **this PC's own traffic** instead.

  Nearby bad slots of one cause form a stretch ("19:42: your Wi-Fi or router was slow for 40 s").
- **Rating:**
  - *Good*: no drop, and spikes rare and short.
  - *Some trouble*: a short drop, a spike of 15 s or more, or two or more stretches an hour.
  - *Poor*: a drop of 10 s or more, trouble in 5% of the time, or twelve or more stretches an hour.
  - Under half a minute of measurements is *too short to judge*.
- **Each lag mark** looks at the 20 seconds before the key press and 5 after:
  - the cause above if there was one;
  - else "this PC was very busy" when the processor ran flat out;
  - else "the game was not online" when it had no connections;
  - else "your connection looked normal at that moment". In that last case the lag was further away (the game's server
    or the route to it), which NetPulse cannot measure; the game's own ping display shows that part.
- **What to do** follows from the causes: a cable for Wi-Fi trouble, pausing downloads, recording a few evenings for the
  provider, closing background programs.

## What NetPulse does not do

- It does not measure in-game latency or the game server's ping, and never presents a reference server as a game server.
- It does not read or capture any traffic. It sees which sockets the game process has open, as Task Manager does.
- It does not need or ask for administrator rights.

## Verification

Everything game-related is verified without a real match and without anyone at the PC:

- **Automated tests** (481) cover:
  - building the game library: built-in games enriched from Windows' list, client programs left out, titles grouped,
    uninstalled games dropped, added and removed games, stable ids, names from product names and folder layouts;
  - reading the real Windows game list (machine-independent assertions);
  - exact-path and name detection;
  - watching all games, automatic start and stop with the grace period, a recording that waits for its game, no false
    "started" after the list is refreshed;
  - the verdict in every situation: steady; home network; provider; own traffic; outage; a noisy server; one server alone;
    one short spike; an HTTPS handshake that varies on its own; too short; gaps;
  - each kind of lag mark; the report and index;
  - the processor counter; pruning of automatic recordings.
- **Self-test inside the published exe:** NetPulse stands in for a game added by the user:
  - it is found by its exact path and recorded automatically through the real process and socket APIs;
  - a lag mark is taken and the session is saved;
  - its TCP and UDP sockets, the automatic flag and the verdict are checked;
  - Windows' game list is read.
- **GUI check of the published exe (UI Automation):** a stand-in game program (`tests/NetPulse.FakeGame`, a UDP
  socket and a TCP connection, like an online game) is added in Settings. Then:
  - the Game Monitor lists it;
  - starting it starts a recording by itself, and lag is marked by button and by the system-wide shortcut;
  - the live verdict appears;
  - closing it saves the recording by itself, and the verdict is shown;
  - the session file holds the game, the automatic flag, the UDP socket and the processor samples;
  - the recording opens in History with its verdict.

What only a real match would add is the feel of it: whether NetPulse's verdicts match what you notice in game. A
recording of your own game sessions is the way to check that, and nothing in the verdict depends on which game it was.
