# How every Lagwise build is verified

`build.ps1` promotes a build to `dist/` only when every step below passes. Its results are written to
`dist/releases/<version>/build-info.json` (test counts, self-test, GUI check, install check, bundled .NET runtime,
checksums).

1. **Release build, warnings as errors.**
2. **Automated tests** (xUnit, `tests/Lagwise.Tests`). Most run against simulated inputs (probe results, process lists,
   socket tables, recordings), and some run against the real Windows APIs:
   - the process list;
   - the TCP and UDP socket tables with sockets the test opens;
   - Windows' game list;
   - the processor counters;
   - live pings, DNS and HTTPS to public servers;
   - the request channel between two starts (a real named pipe), and the startup entry with Task Manager's switch for it
     (under a scratch registry key, never the real one).

   Windows behaviour that cannot be produced on demand is driven with recorded inputs: sleep and wake (cycles before,
   during and after), Wi-Fi connections read from the WLAN API (byte-exact blocks, also for a PC without Wi-Fi), the
   Windows 11 location message of `netsh`, and adapter choice with VPNs, tunnels and virtual switches.
3. **Self-contained publish** with the latest .NET 10 runtime patch named by Microsoft's release feed. The build fails
   if the published exe reports another runtime.
4. **Self-test inside the published exe** (`Lagwise.exe --selftest`):
   - the built-in games are read, and Windows' game list is read;
   - the data folder is outside the program folder;
   - the real process list shows this process with its path;
   - the real socket tables show its own TCP and UDP sockets;
   - the exe stands in for a game added by the user: it must be found by its exact path and recorded automatically, a lag
     mark is taken, and the saved recording must hold its sockets, the automatic flag and a verdict;
   - settings survive a restart.
5. **GUI check of the published exe** (`scripts/Verify-Gui.ps1`, UI Automation, isolated data folder), about 70 checks,
   among them:
   - first run, Beginner and Advanced, theme, overlay, Ready to play?, Copy summary and Export report;
   - the Game Monitor end to end with `tests/Lagwise.FakeGame` (a stand-in online game added as a user adds one):
     - it is listed;
     - starting it starts a recording by itself;
     - lag is marked by button, by the system-wide shortcut and by the jump list's task (a second start of the exe);
     - the live verdict appears;
     - closing it saves the recording by itself, and the verdict is shown;
     - the session file holds the game, its UDP socket and the processor samples;
     - the recording opens in History with its verdict, graph and exports;
   - History, Network activity, Site check, Security check, Connection diary, Speed test and Bufferbloat;
   - every page opened once without an interface error;
   - a changed setting is applied and written;
   - a second start hands over to the running Lagwise, the jump list's History task switches its page, and being asked
     to exit (as an uninstall does) saves and closes it.
6. **Install check** (`scripts/Verify-Install.ps1`, headless, skip with `-SkipInstallCheck`): the published Lagwise.exe
   installs itself with `--install --install-dir <temporary folder> --no-shortcuts`, so the real Start menu and
   installation folder are never touched; the Settings > Apps entry is checked (version, uninstall command); the
   installed copy runs its self-test; `--uninstall --quiet` removes the entry, the program and its folder, and the
   downloaded copy is left as it was (9 checks). It skips itself when a real Lagwise installation is registered on
   the build PC, since it would replace it.

Nothing here needs a real game, a real match or a person at the PC. It does need an interactive desktop for the GUI
check, so the mouse and keyboard should be left alone while it runs.

For looking at a build without a screen, `Lagwise.exe --screenshot "GameMonitor=gm.png;History=h.png"` renders pages
to PNG files in software and closes. `History:<recording id>` opens a given recording; `--screenshot-window` renders the
whole window (title bar and sidebar) instead of the whole page.

Not verified on this build machine, which has no Wi-Fi adapter: reading a live Wi-Fi connection through the WLAN API.
Its parsing is tested byte for byte, the API is called only where Windows has a WLAN service, and the earlier `netsh`
reading remains as the fallback.
