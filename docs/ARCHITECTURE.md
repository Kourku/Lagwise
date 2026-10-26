# Lagwise architecture

Lagwise is a .NET 10 WPF application for Windows 10 and 11 (x64), published as one self-contained executable with
its own .NET runtime. It has no third-party libraries, no server and no account; everything it keeps lives in
`%LOCALAPPDATA%\Lagwise`.

## Projects

| Project | What it is |
|---|---|
| `src/Lagwise.Core` | Everything that measures, decides and stores. No UI types, so it is tested directly. |
| `src/Lagwise.App` | The WPF application: windows, view models, custom drawn controls (charts, gauges, status marks), tray, overlay, global shortcut, taskbar (jump list, badge, preview buttons). |
| `tests/Lagwise.Tests` | xUnit tests for Core, including tests against the real Windows APIs (process list, socket tables, game list). |
| `tests/Lagwise.FakeGame` | `StandInGame.exe`, a stand-in "online game" for the GUI check. Never shipped. |

## Composition

`LagwiseHost` (Core) builds every service once. The main window, the tray, the mini overlay and the Game Monitor all
share one measurement engine, one event log, one settings object and one session recorder.

```
                ┌──────────────────────── Lagwise.App (WPF, MVVM) ────────────────────────┐
                │ MainWindow · OverlayWindow · Tray · GlobalHotkey · TaskbarTasks · Snapshots │
                │ MainViewModel · GameMonitorViewModel · HistoryViewModel · VerdictViewModel │
                │ SiteCheck / Security / Diary / Activity / Settings view models             │
                └───────────────────────────────────┬────────────────────────────────────────┘
                                                    │ bindings, events
┌───────────────────────────────────────── LagwiseHost (Core) ─────────────────────────────────────────┐
│ MeasurementEngine ── Ping · TCP · HTTPS · DNS probes ── DiagnosticEngine ── EventLogService            │
│ SessionRecorder ── SessionStore ── SessionAnalysis · SessionVerdict · SessionReportBuilder             │
│ GameMonitorService ── GameLibrary (built-in · Windows' game list · added) ── GameWatcher ── detection   │
│ AdapterActivityMonitor · ProcessorLoad · OnlinePrograms · ConnectionDiary · TestHistory · BaselineMemory│
│ Bufferbloat · SpeedTest · Traceroute/MTR · SiteCheckService · SecurityCheckService · SettingsService     │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Core, by folder

- **Services**: the measurement engine (one probe per target per interval, rolling windows, baselines, episodes and
  events, and the grace after Windows wakes), the probes, the live diagnosis, network discovery (the adapter Windows
  routes internet traffic through, from `GetBestInterface`; VPNs and tunnels; Wi-Fi from the WLAN API in `WlanInfo`, with
  `netsh` by full path as a fallback), the startup entry (`WindowsIntegrationService`), adapter activity and processor load, bufferbloat and speed tests, route tracing, the connection diary, stored test
  results, report export and privacy (`ReportPrivacy`), and the composition root.
- **Games**: the built-in game profiles, `GameLibrary` (reads Windows' game list from
  `HKCU\System\GameConfigStore\Children`, names games, merges built-in, Windows and added games), process detection
  (by name, path hint or exact path), `GameWatcher` (start, exit and restart from successive process lists),
  connection discovery (`GetExtendedTcpTable` / `GetExtendedUdpTable`), `GameMonitorService` (watching, automatic
  recording, lag marks) and the self-test run by `Lagwise.exe --selftest`.
- **Sessions**: the recording model, the recorder (samples, events, gaps, drops, lag marks, traffic, processor and Wi-Fi
  each second, the game's processes and sockets), the store (one JSON file per recording plus a summary index), and the
  analysis: statistics, comparisons, time-of-day patterns, the **session verdict** and the Markdown report.
- **Sites** and **Security**: the Site check (DNS against DNS over HTTPS, connection trace, status pages) and the
  Security check (firewall, network type, Wi-Fi protection, listeners, DNS honesty, HTTPS inspection, UPnP).
- **Statistics**: medians and percentiles, rolling windows, and the long-term memory of each target's usual ping.
- `SystemTools` (Windows tools only by full path), `UntrustedText` (cleaning text from the network, CSV fields),
  `Lexicon` (every term and event label shown to people), `AppInstance` (one running copy per data folder).

## Windows integration

- **One copy per data folder.** `App` holds a named mutex derived from the data folder. A later start sends its request
  (`show`, `mark-lag`, `record`, `overlay`, `speedtest`, `bufferbloat`, `history`, `exit`) over a named pipe created with
  `PipeOptions.CurrentUserOnly` and exits; anything but those words is dropped. The taskbar jump list starts
  `Lagwise.exe --command ...`, and an uninstall sends `exit`. A test copy with its own `LAGWISE_DATA_DIR` runs alongside.
- **Sleep and wake.** `SystemEvents.PowerModeChanged` calls `LagwiseHost.OnSystemSuspending` (checkpoint the recording,
  save the diary) and `OnSystemResumed`. Until an internet server answers again, or 45 s have passed, cycles without an
  internet answer are not ingested, so a recording shows a gap rather than a drop. The measuring loop also notices a
  pause it did not take (more than 15 s) in case the notice is missed.
- **Installing itself.** There is no separate setup program: `SelfInstaller` (Core) copies the running Lagwise.exe to
  `%LOCALAPPDATA%\Programs\Lagwise` (through a `.partial` file, so a failure never leaves half a program), adds the
  Start menu entry (and a desktop shortcut if asked), registers `HKCU\...\Uninstall\Lagwise` with
  `UninstallString = "Lagwise.exe" --uninstall`, moves the Run entry along when it started the downloaded copy, and
  removes an installed NetPulse. `Installation` (App) offers it (Overview card once, Settings > Installation), then
  starts the installed copy with `--after-install <pid>`, which waits for the downloaded copy to exit before claiming
  the single-instance mutex. `--uninstall` closes a running copy (pipe `exit`), removes the shortcuts that start this
  copy, the Apps and startup entries, and deletes Lagwise.exe and its folder with a delayed `cmd.exe` (from the Windows
  folder) once the process has exited. Not offered in the Store edition or in a test run with its own data folder.
- **Sign-out and shutdown.** `App.OnSessionEnding` lets the window close for real; `OnExit` disposes the host, which
  saves a recording in progress.
- **Taskbar and tray.** `TaskbarTasks` builds the jump list and the images for the badge (`TaskbarItemInfo.Overlay`) and
  the preview buttons; the tray icon is drawn with a status dot. Test progress uses `TaskbarItemInfo.ProgressValue`.
- **Window.** `app.manifest` declares per-monitor DPI awareness (v2) and Windows 10/11. `MainWindow` answers
  `WM_NCHITTEST` with `HTMAXBUTTON` over its own maximise button, so Windows 11 shows Snap Layouts. The overlay window
  has `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, and `WS_EX_TRANSPARENT` when clicks should pass through.

## Threads

- Probing runs on the thread pool (`Task.WhenAll` per cycle); the UI thread never waits for the network.
- The Game Monitor polls the process list on one background loop (every 5 s while watching, 2 s while recording)
  and reads socket tables only while a recorded game runs.
- View models coalesce updates and refresh on the dispatcher at background priority. Heavy work (verdicts, loading
  recordings, reading the game list) runs on the thread pool.
- Recordings are checkpointed every 30 s and saved on stop and on shutdown, so a crash loses at most half a minute.

## Storage

`%LOCALAPPDATA%\Lagwise` (or `LAGWISE_DATA_DIR` for tests):

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
5. The install check (`scripts/Verify-Install.ps1`): the published exe installs itself into a temporary folder, the
   installed copy runs its self-test, and it uninstalls itself again.
6. Only a build that passes everything is promoted to `dist/`.

See [VERIFICATION.md](VERIFICATION.md).
