# NetPulse architecture

NetPulse is a .NET 10 WPF application for Windows 10 and 11 (x64), published as one self-contained executable with
its own .NET runtime. It has no third-party libraries, no server and no account; everything it keeps lives in
`%LOCALAPPDATA%\NetPulse`.

## Projects

| Project | What it is |
|---|---|
| `src/NetPulse.Core` | Everything that measures, decides and stores. No UI types, so it is tested directly. |
| `src/NetPulse.App` | The WPF application: windows, view models, custom drawn controls (charts, gauges, status marks), tray, overlay, global shortcut. |
| `src/NetPulse.Installer` | `NetPulse-Setup.exe`, a per-user installer (no administrator rights). |
| `tests/NetPulse.Tests` | xUnit tests for Core, including tests against the real Windows APIs (process list, socket tables, game list). |
| `tests/NetPulse.FakeGame` | `StandInGame.exe`, a stand-in "online game" for the GUI check. Never shipped. |

## Composition

`NetPulseHost` (Core) builds every service once. The main window, the tray, the mini overlay and the Game Monitor all
share one measurement engine, one event log, one settings object and one session recorder.

```
                ┌──────────────────────── NetPulse.App (WPF, MVVM) ────────────────────────┐
                │ MainWindow · OverlayWindow · Tray · GlobalHotkey · PageSnapshots           │
                │ MainViewModel · GameMonitorViewModel · HistoryViewModel · VerdictViewModel │
                │ SiteCheck / Security / Diary / Activity / Settings view models             │
                └───────────────────────────────────┬────────────────────────────────────────┘
                                                    │ bindings, events
┌───────────────────────────────────────── NetPulseHost (Core) ─────────────────────────────────────────┐
│ MeasurementEngine ── Ping · TCP · HTTPS · DNS probes ── DiagnosticEngine ── EventLogService            │
│ SessionRecorder ── SessionStore ── SessionAnalysis · SessionVerdict · SessionReportBuilder             │
│ GameMonitorService ── GameLibrary (built-in · Windows' game list · added) ── GameWatcher ── detection   │
│ AdapterActivityMonitor · ProcessorLoad · OnlinePrograms · ConnectionDiary · TestHistory · BaselineMemory│
│ Bufferbloat · SpeedTest · Traceroute/MTR · SiteCheckService · SecurityCheckService · SettingsService     │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Core, by folder

- **Services**: the measurement engine (one probe per target per interval, rolling windows, baselines, episodes and
  events), the probes, the live diagnosis, network discovery (adapter, gateway, Wi-Fi via `netsh` by full path),
  adapter activity and processor load, bufferbloat and speed tests, route tracing, the connection diary, stored test
  results, report export and privacy (`ReportPrivacy`), and the composition root.
- **Games**: the built-in game profiles, `GameLibrary` (reads Windows' game list from
  `HKCU\System\GameConfigStore\Children`, names games, merges built-in, Windows and added games), process detection
  (by name, path hint or exact path), `GameWatcher` (start, exit and restart from successive process lists),
  connection discovery (`GetExtendedTcpTable` / `GetExtendedUdpTable`), `GameMonitorService` (watching, automatic
  recording, lag marks) and the self-test run by `NetPulse.exe --selftest`.
- **Sessions**: the recording model, the recorder (samples, events, gaps, drops, lag marks, traffic, processor and Wi-Fi
  each second, the game's processes and sockets), the store (one JSON file per recording plus a summary index), and the
  analysis: statistics, comparisons, time-of-day patterns, the **session verdict** and the Markdown report.
- **Sites** and **Security**: the Site check (DNS against DNS over HTTPS, connection trace, status pages) and the
  Security check (firewall, network type, Wi-Fi protection, listeners, DNS honesty, HTTPS inspection, UPnP).
- **Statistics**: medians and percentiles, rolling windows, and the long-term memory of each target's usual ping.
- `SystemTools` (Windows tools only by full path), `UntrustedText` (cleaning text from the network, CSV fields),
  `Lexicon` (every term and event label shown to people).

## Threads

- Probing runs on the thread pool (`Task.WhenAll` per cycle); the UI thread never waits for the network.
- The Game Monitor polls the process list on one background loop (every 5 s while watching, 2 s while recording)
  and reads socket tables only while a recorded game runs.
- View models coalesce updates and refresh on the dispatcher at background priority. Heavy work (verdicts, loading
  recordings, reading the game list) runs on the thread pool.
- Recordings are checkpointed every 30 s and saved on stop and on shutdown, so a crash loses at most half a minute.

## Storage

`%LOCALAPPDATA%\NetPulse` (or `NETPULSE_DATA_DIR` for tests):

- `settings.json`, `sessions\*.session.json` and `sessions\index.json`;
- `test-history.json`, `diary.json`, `baselines.json`;
- `game-icons\`, `site-icons\` and `logs\errors.log`.

Files are written to a temporary file and moved into place. See [SECURITY.md](../SECURITY.md) for what is sent, read
and kept.

## Build and release

`build.ps1`:
1. Release build with warnings as errors.
2. Tests.
3. Self-contained single-file publish with the latest .NET 10 runtime patch (read from Microsoft's release feed).
4. The published exe's self-test, then the GUI check (`scripts/Verify-Gui.ps1`, UI Automation, with the stand-in game).
5. Optionally the installer and its install/uninstall check.
6. Only a build that passes everything is promoted to `dist/`.

See [VERIFICATION.md](VERIFICATION.md).
