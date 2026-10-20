# How every NetPulse build is verified

`build.ps1` promotes a build to `dist/` only when every step below passes. Its results are written to
`dist/releases/<version>/build-info.json` (test counts, self-test, GUI check, installer check, bundled .NET runtime,
checksums).

1. **Release build, warnings as errors.**
2. **Automated tests** (xUnit, `tests/NetPulse.Tests`). Most run against simulated inputs (probe results, process lists,
   socket tables, recordings), and some run against the real Windows APIs:
   - the process list;
   - the TCP and UDP socket tables with sockets the test opens;
   - Windows' game list;
   - the processor counters;
   - live pings, DNS and HTTPS to public servers.
3. **Self-contained publish** with the latest .NET 10 runtime patch named by Microsoft's release feed. The build fails
   if the published exe reports another runtime.
4. **Self-test inside the published exe** (`NetPulse.exe --selftest`):
   - the built-in games are read, and Windows' game list is read;
   - the data folder is outside the program folder;
   - the real process list shows this process with its path;
   - the real socket tables show its own TCP and UDP sockets;
   - the exe stands in for a game added by the user: it must be found by its exact path and recorded automatically, a lag
     mark is taken, and the saved recording must hold its sockets, the automatic flag and a verdict;
   - settings survive a restart.
5. **GUI check of the published exe** (`scripts/Verify-Gui.ps1`, UI Automation, isolated data folder), about 65 checks,
   among them:
   - first run, Beginner and Advanced, theme, overlay, Ready to play?, Copy summary and Export report;
   - the Game Monitor end to end with `tests/NetPulse.FakeGame` (a stand-in online game added as a user adds one):
     - it is listed;
     - starting it starts a recording by itself;
     - lag is marked by button and by the system-wide shortcut;
     - the live verdict appears;
     - closing it saves the recording by itself, and the verdict is shown;
     - the session file holds the game, its UDP socket and the processor samples;
     - the recording opens in History with its verdict, graph and exports;
   - History, Network activity, Site check, Security check, Connection diary, Speed test and Bufferbloat;
   - every page opened once without an interface error;
   - a changed setting is applied and written.
6. **Installer** (with `-VerifyInstaller`): installed into a temporary folder and uninstalled again through UI
   Automation (7 checks).

Nothing here needs a real game, a real match or a person at the PC. It does need an interactive desktop for the GUI
check, so the mouse and keyboard should be left alone while it runs.

For looking at a build without a screen, `NetPulse.exe --screenshot "GameMonitor=gm.png;History=h.png"` renders pages
to PNG files in software and closes. `History:<recording id>` opens a given recording.
