using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Core.Games;

public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

public sealed class SelfTestReport
{
    public string Version { get; set; } = string.Empty;
    /// <summary>The .NET runtime inside the executable (a published build carries its own and is not patched by Windows Update).</summary>
    public string Runtime { get; set; } = Environment.Version.ToString();
    public string ExecutablePath { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public bool Ok => Checks.All(c => c.Passed);
    public List<SelfTestCheck> Checks { get; set; } = new();
}

/// <summary>
/// Headless end-to-end check of the Game Monitor pipeline using real Windows APIs. It runs inside the published executable
/// (<c>Lagwise.exe --selftest</c>) and uses this process as a stand-in "game": the real process list, the real TCP/UDP socket
/// tables, a real session file. It proves the published build works; it does NOT validate any real game.
/// </summary>
public static class SelfTest
{
    public static SelfTestReport Run(string dataDirectory)
    {
        var report = new SelfTestReport
        {
            DataDirectory = dataDirectory,
            ExecutablePath = Environment.ProcessPath ?? "",
            Version = typeof(SelfTest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "unknown"
        };

        void Check(string name, Func<(bool Ok, string Detail)> body)
        {
            try
            {
                var (ok, detail) = body();
                report.Checks.Add(new SelfTestCheck(name, ok, detail));
            }
            catch (Exception ex)
            {
                report.Checks.Add(new SelfTestCheck(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        Check("Built-in games and Windows' game list are read", () =>
        {
            var windows = OperatingSystem.IsWindows() ? new WindowsGameListSource().Read() : Array.Empty<WindowsGameEntry>();
            var library = GameLibrary.Build(GameProfiles.All, windows, Array.Empty<string>());
            bool ok = GameProfiles.All.Count == 7 && library.Count >= GameProfiles.All.Count && windows.All(w => Path.IsPathFullyQualified(w.ExePath));
            return (ok, $"{GameProfiles.All.Count} built-in games; Windows lists {windows.Count} program(s); {library.Count} game(s) in the library");
        });

        Check("Data directory is outside the executable folder", () =>
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            bool outside = !Path.GetFullPath(dataDirectory).StartsWith(exeDir, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(exeDir);
            return (outside, $"data={dataDirectory}, exe={exeDir}");
        });

        // a loopback TCP pair and a UDP socket owned by this process, so the real socket tables must contain them
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, listenPort);
        using var accepted = listener.AcceptTcpClient();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        string selfName = Process.GetCurrentProcess().ProcessName;

        Check("Real process list contains this process with start time and path", () =>
        {
            var me = new SystemProcessSource(new[] { selfName }).GetProcesses().FirstOrDefault(p => p.Pid == Environment.ProcessId);
            return (me?.StartTimeUtc != null && !string.IsNullOrEmpty(me.ExecutablePath), me == null ? "not found" : $"{me.Name} PID {me.Pid} {me.ExecutablePath}");
        });

        Check("Real TCP and UDP socket tables report this process's sockets", () =>
        {
            var snap = new WindowsConnectionSource().Capture();
            var mine = snap.Connections.Where(c => c.Pid == Environment.ProcessId).ToList();
            bool tcp = mine.Any(c => c.Protocol == TransportProtocol.Tcp && c.State == ConnectionState.Established && c.LocalPort == clientPort && c.RemotePort == listenPort);
            bool udpFound = mine.Any(c => c.Protocol == TransportProtocol.Udp && c.LocalPort == udpPort && c.RemoteAddress == null);
            return (tcp && udpFound, $"{snap.Connections.Count} sockets total, {mine.Count} owned by this process; tcp={tcp} udp={udpFound}; warnings={snap.Warnings.Count}");
        });

        Check("A game is detected by its exact path and recorded automatically, with real process and socket APIs", () =>
        {
            // this program stands in for a game the user added: found by its exact location, like any game on any PC
            string exe = Environment.ProcessPath ?? "";
            var library = GameLibrary.Build(Array.Empty<GameProfile>(), Array.Empty<WindowsGameEntry>(), new[] { exe });
            if (library.Count != 1) return (false, $"the stand-in game was not built from {exe}");

            using var host = new LagwiseHost(dataDirectory, profiles: Array.Empty<GameProfile>(), gameList: new NoGames());
            host.Settings.AutoRecordGame = true;
            var monitor = host.GameMonitor;
            monitor.SetLibrary(library);
            monitor.Select(GameMonitorService.AllGames, out _);

            monitor.PollOnce();                                   // the game is running: recording starts by itself
            bool autoStarted = monitor.IsMonitoring && monitor.StartedAutomatically;
            Thread.Sleep(2200);
            monitor.PollOnce();
            monitor.MarkLag("self-test marker");

            var snap = monitor.Snapshot();
            bool running = snap.Process?.State == GameRunState.Running && snap.Game?.Source == GameSource.AddedByYou;
            bool online = snap.UdpSockets >= 1;

            var session = monitor.Stop(out var error);
            if (session == null || error != null) return (false, $"stop failed: {error}");

            var saved = host.Sessions.Load(session.Id);
            bool tcp = saved?.Connections.Any(c => c.Protocol == "TCP" && c.Remote != null && c.Remote.Contains(listenPort.ToString())) == true;
            bool udp = saved?.Connections.Any(c => c.Protocol == "UDP" && c.Local.Contains(udpPort.ToString())) == true;
            bool persisted = saved is { IsComplete: true, Automatic: true } && saved.LagMarkers.Count == 1 && saved.GameProcesses.Count == 1
                && saved.GameDisplayName == library[0].DisplayName;
            var summary = host.Sessions.List().FirstOrDefault(x => x.Id == session.Id);
            bool verdict = summary?.Verdict != null && !string.IsNullOrEmpty(summary.VerdictHeadline);

            return (autoStarted && running && online && tcp && udp && persisted && verdict,
                $"autoStarted={autoStarted} running={running} online={online} tcp={tcp} udp={udp} persisted={persisted} verdict={summary?.Verdict} " +
                $"(game \"{library[0].DisplayName}\", connections={saved?.Connections.Count}, file under {host.Sessions.Directory})");
        });

        Check("Settings persist across restarts", () =>
        {
            using (var host = new LagwiseHost(dataDirectory))
            {
                host.Settings.IsBeginnerMode = false;
                host.Settings.LastGameId = "lol";
                if (!host.SaveSettings(out var err)) return (false, err ?? "save failed");
            }
            using var again = new LagwiseHost(dataDirectory);
            bool ok = !again.Settings.IsBeginnerMode && again.Settings.LastGameId == "lol";
            return (ok, $"settings file {again.SettingsService.FilePath}");
        });

        return report;
    }

    private sealed class NoGames : IGameListSource
    {
        public IReadOnlyList<WindowsGameEntry> Read() => Array.Empty<WindowsGameEntry>();
    }

    public static string ToJson(SelfTestReport report) =>
        JsonSerializer.Serialize(new
        {
            report.Ok, report.Version, report.Runtime, report.ExecutablePath, report.DataDirectory,
            Checks = report.Checks.Select(c => new { c.Name, c.Passed, c.Detail })
        }, new JsonSerializerOptions { WriteIndented = true });
}
