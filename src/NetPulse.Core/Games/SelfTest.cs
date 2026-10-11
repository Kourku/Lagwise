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
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Games;

public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

public sealed class SelfTestReport
{
    public string Version { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public bool Ok => Checks.All(c => c.Passed);
    public List<SelfTestCheck> Checks { get; set; } = new();
}

/// <summary>
/// Headless end-to-end check of the Game Monitor pipeline using real Windows APIs. It runs inside the published executable
/// (<c>NetPulse.exe --selftest</c>) and uses this process as a stand-in "game": the real process list, the real TCP/UDP socket
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

        Check("Seven game profiles, none Supported without live validation", () =>
        {
            bool ok = GameProfiles.All.Count == 7 && GameProfiles.All.All(p => p.Status != SupportStatus.Supported);
            return (ok, string.Join(", ", GameProfiles.All.Select(p => $"{p.Id}={p.Status}")));
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

        Check("Game Monitor records a session using real process and socket APIs", () =>
        {
            var profile = new GameProfile
            {
                Id = "selftest", DisplayName = "NetPulse self-test (stand-in game)", Publisher = "n/a",
                ProcessRules = new[] { new ProcessRule(selfName, ProcessRole.Game) },
                Capabilities = new GameCapabilities(true, true, true, true, false),
                StatusPage = new StatusResource(null, false, "n/a"),
                Limitations = new[] { "Self-test profile: this application's own process stands in for a game." }
            };

            using var host = new NetPulseHost(dataDirectory, profiles: new[] { profile });
            var monitor = host.GameMonitor;
            monitor.Select("selftest", out _);
            if (!monitor.Start(out var startMsg)) return (false, startMsg);

            monitor.PollOnce();
            Thread.Sleep(2200);
            monitor.PollOnce();
            monitor.MarkLag("self-test marker");

            var snap = monitor.Snapshot();
            bool running = snap.Process?.State == GameRunState.Running;
            bool sawTcp = snap.Connections.Any(c => c.Protocol == "TCP" && c.Remote.Contains(listenPort.ToString()));
            bool sawUdp = snap.Connections.Any(c => c.Protocol == "UDP" && c.Local.Contains(udpPort.ToString()));
            bool refusedLoopback = false;
            var loop = snap.Connections.FirstOrDefault(c => c.HasRemote && c.Scope.StartsWith("This PC"));
            if (loop != null) refusedLoopback = !monitor.MeasureEndpoint(loop.Id, EndpointMethod.Icmp, out _);

            var session = monitor.Stop(out var error);
            if (session == null || error != null) return (false, $"stop failed: {error}");

            var saved = host.Sessions.Load(session.Id);
            bool persisted = saved is { IsComplete: true } && saved.LagMarkers.Count == 1 && saved.Connections.Count > 0
                && saved.Connections.All(c => c.Role == "Unknown") && saved.GameProcesses.Count == 1 && saved.Limitations.Count > 0;

            return (running && sawTcp && sawUdp && refusedLoopback && persisted,
                $"running={running} tcp={sawTcp} udp={sawUdp} loopbackRefused={refusedLoopback} persisted={persisted} " +
                $"(samples={saved?.Samples.Count}, connections={saved?.Connections.Count}, file under {host.Sessions.Directory})");
        });

        Check("Settings persist across restarts", () =>
        {
            using (var host = new NetPulseHost(dataDirectory))
            {
                host.Settings.IsBeginnerMode = false;
                host.Settings.LastGameId = "lol";
                if (!host.SaveSettings(out var err)) return (false, err ?? "save failed");
            }
            using var again = new NetPulseHost(dataDirectory);
            bool ok = !again.Settings.IsBeginnerMode && again.Settings.LastGameId == "lol";
            return (ok, $"settings file {again.SettingsService.FilePath}");
        });

        return report;
    }

    public static string ToJson(SelfTestReport report) =>
        JsonSerializer.Serialize(new
        {
            report.Ok, report.Version, report.ExecutablePath, report.DataDirectory,
            Checks = report.Checks.Select(c => new { c.Name, c.Passed, c.Detail })
        }, new JsonSerializerOptions { WriteIndented = true });
}
