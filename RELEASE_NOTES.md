# NetPulse release notes

Newest first. Versions follow the repository names (NetPulse-ver0.1, -ver0.3); builds made before 0.3.0 were numbered 1.0.0-1.2.0. `build.ps1` copies the section matching the version in `Directory.Build.props` into every verified release.

## 0.3.0

**New: Settings** - test frequency, recent window and history length, which events are recorded and at what limits, which reference servers are monitored (add your own, switch off or remove), tray and start-up behaviour, and where your data lives. Changes apply immediately and are validated before they are saved.

**New: History & reports** - browse every recording, open one to see its graph (latency, variation or probe loss per target, with lag markers, monitoring gaps and outages), per-target statistics, what the measurements showed around each moment you marked as laggy, the stored findings and events, then export a Markdown report or the raw probe data (CSV).

**Visual refresh** - Segoe UI Variable text, Segoe Fluent icons, a new dark palette and type scale, a sidebar with an accent indicator, a dark title bar, slim dark scrollbars, themed controls (dropdowns, switches, tables) and larger, clearer text. Honest wording throughout (Reference ping, Probe loss, Latency variation), a Help & glossary page, and a Game Monitor / latest-issue summary on the Overview.

**Fixes** - the analyzer no longer treats a consistently unsteady reference (for example a public resolver rate-limiting pings) as evidence of a new problem; tables no longer swallow the mouse wheel; Advanced-only columns are hidden correctly on first visit; Minimise/Close to tray and Start with Windows now follow their settings.

## 0.2.0

**New: Game Monitor** (Reference-only for all seven games - see `docs/GAME_MONITOR.md` for exactly what is and is not verified)
- Profiles for League of Legends, Fortnite, Apex Legends, Counter-Strike 2, World of Warcraft, Marvel Rivals and Path of Exile, with process rules, limitations and (where one exists) the official status page.
- Detects the game process starting, exiting and restarting; launchers, anti-cheat and crash reporters are never mistaken for the game.
- Lists the TCP connections (IPv4/IPv6) and UDP sockets the game process has open, through Windows' own socket tables - no administrator rights. UDP remote peers are not visible to Windows without packet tracing, and the app says so.
- Endpoints are labelled "Unknown role"; none is claimed to be the match server. You can choose an endpoint to measure (ICMP or TCP connect) and the app explains when a method is not meaningful.
- Start / Stop & save / Mark lag, connection-change timeline, monitoring-gap tracking, and findings that separate observations, possible explanations, confidence and the next test.

**Also**
- Settings persist; one shared service host for window, tray and Game Monitor.
- Loss and variation use a recent window; duplicate gateway/DNS events are collapsed; the spike threshold setting is honoured.
- Speed Test and Bufferbloat verify their provider first and report failures instead of fake results; external service use is disclosed in the UI.
- Portable build self-test (`NetPulse.exe --selftest`) and a verified-release pipeline (`build.ps1`).

**Known limitations**
- No game has been validated against a live match yet; all profiles stay Reference-only.
- Process names of six of the seven games come from documentation, not a real installation.
- Not game-server ping: in-game latency is never measured.

## 0.1.0

Initial NetPulse release (network quality dashboard, MTR, Speed Test, Bufferbloat, event timeline, exports).
