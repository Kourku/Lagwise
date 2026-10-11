using System.Net;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

public sealed class FakeProcessSource : IProcessSource
{
    public List<ProcessInfo> Current { get; } = new();
    public IReadOnlyList<ProcessInfo> GetProcesses() { lock (Current) return Current.ToList(); }
}

public sealed class FakeConnectionSource : IConnectionSource
{
    public List<RawConnection> Current { get; } = new();
    public List<string> Warnings { get; } = new();
    public int Captures;
    public ConnectionSnapshot Capture()
    {
        Interlocked.Increment(ref Captures);
        lock (Current) return new ConnectionSnapshot(Current.ToList(), Warnings.ToList(), DateTime.UtcNow);
    }
}

public class GameMonitorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string GamePath = @"E:\Riot Games\League of Legends\Game\League of Legends.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpulse-monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessSource _procs = new();
    private readonly FakeConnectionSource _conns = new();
    private readonly NetPulseHost _host;
    private int _tick;

    public GameMonitorTests()
    {
        _host = new NetPulseHost(_dir, _procs, _conns);
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ProcessInfo Game(int pid = 6000) => new(pid, "League of Legends", T0.AddMinutes(1), GamePath);
    private static ProcessInfo Client() => new(5000, "LeagueClient", T0, @"E:\Riot Games\League of Legends\LeagueClient.exe");

    private static RawConnection Tcp(string ip, int port, int pid = 6000, int local = 50000) =>
        new(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.20"), local, IPAddress.Parse(ip), port, ConnectionState.Established, pid);

    private static RawConnection Udp(int port, int pid = 6000) =>
        new(TransportProtocol.Udp, IPAddress.Any, port, null, 0, ConnectionState.NotApplicable, pid);

    private GameMonitorService M => _host.GameMonitor;

    private void Poll(int times = 1) { for (int i = 0; i < times; i++) { _tick++; M.PollOnce(); } }

    [Fact]
    public void Selecting_ADetectsNothingWhenGameIsAbsent_AndReportsLauncherOnlyHonestly()
    {
        Assert.True(M.Select("lol", out _));
        _procs.Current.Add(new ProcessInfo(1, "RiotClientServices", T0, @"E:\Riot Games\Riot Client\RiotClientServices.exe"));
        Poll();

        var snap = M.Snapshot();
        Assert.Equal(GameRunState.LauncherOnly, snap.Process!.State);
        Assert.False(snap.IsMonitoring);
        Assert.Equal(0, _conns.Captures); // no socket enumeration unless monitoring a running game
    }

    [Fact]
    public void FullWorkflow_RecordsProcessesConnectionsMeasurementsLagAndFindings()
    {
        M.Select("lol", out _);
        Assert.True(M.Start(out _));
        Assert.True(_host.Recorder.IsRecording);

        // monitoring started before the game: waiting, nothing captured
        Poll();
        Assert.Equal(0, _conns.Captures);

        // game launches
        _procs.Current.AddRange(new[] { Client(), Game() });
        _conns.Current.AddRange(new[] { Tcp("34.1.2.3", 443), Tcp("52.9.9.9", 8393, local: 50001), Udp(7777), Tcp("127.0.0.1", 2999, local: 50002) });
        Poll(2);

        var snap = M.Snapshot();
        Assert.Equal(GameRunState.Running, snap.Process!.State);
        Assert.Equal(4, snap.Connections.Count);
        Assert.Equal(2, snap.DistinctRemoteEndpoints);        // loopback + UDP socket don't count as remote endpoints
        Assert.Equal(1, snap.UdpSocketCount);
        Assert.All(snap.Connections, c => Assert.Equal("Unknown role", c.Role));
        Assert.Contains(_host.Events.GetRecentEvents(100), e => e.EventType == "GameProcess" && e.Description.Contains("detected"));
        Assert.Contains(_host.Events.GetRecentEvents(100), e => e.EventType == "ConnectionChange");

        // measuring: a public TCP endpoint is allowed, a UDP socket / loopback is refused with an explanation
        var tcpConn = snap.Connections.Single(c => c.Remote == "34.1.2.3:443");
        Assert.True(M.MeasureEndpoint(tcpConn.Id, EndpointMethod.Icmp, out var info));
        Assert.Contains("filter", info);
        Assert.False(M.MeasureEndpoint(tcpConn.Id, EndpointMethod.Icmp, out _)); // already measured
        Assert.True(M.MeasureEndpoint(tcpConn.Id, EndpointMethod.TcpConnect, out _));

        var udp = snap.Connections.Single(c => c.Protocol == "UDP");
        Assert.False(M.MeasureEndpoint(udp.Id, EndpointMethod.Icmp, out var why));
        Assert.Contains("remote peer", why);
        var loop = snap.Connections.Single(c => c.Scope.StartsWith("This PC"));
        Assert.False(M.MeasureEndpoint(loop.Id, EndpointMethod.Icmp, out _));

        Assert.Equal(2, _host.Engine.DynamicTargets.Count);
        Assert.Equal(2, M.Snapshot().Measured.Count);
        Assert.Contains("ICMP", M.Snapshot().Connections.Single(c => c.Id == tcpConn.Id).MeasuredBy);

        // user marks lag, a connection goes away, then the game exits
        Assert.True(M.MarkLag("rubber banding"));
        _conns.Current.RemoveAll(c => c.RemoteAddress?.ToString() == "52.9.9.9");
        Poll(3);
        _procs.Current.Remove(_procs.Current.Single(p => p.Name == "League of Legends"));
        _conns.Current.Clear();
        Poll(1);
        Assert.Equal(GameRunState.LauncherOnly, M.Snapshot().Process!.State);
        Assert.DoesNotContain(M.Snapshot().Connections, c => c.IsActive);

        var session = M.Stop(out var error)!;
        Assert.Null(error);
        Assert.False(M.IsMonitoring);
        Assert.Empty(_host.Engine.DynamicTargets); // measured endpoints stop being probed
        Assert.False(_host.Recorder.IsRecording);

        // the saved file contains everything
        var saved = _host.Sessions.Load(session.Id)!;
        Assert.True(saved.IsComplete);
        Assert.Equal("lol", saved.GameId);
        Assert.Equal("League of Legends", saved.GameDisplayName);
        Assert.Equal("ReferenceOnly", saved.GameSupportStatus);
        Assert.NotEmpty(saved.Limitations);
        Assert.Contains(saved.Limitations, l => l.Contains("UDP"));
        Assert.Equal("rubber banding", Assert.Single(saved.LagMarkers).Note);
        Assert.Contains(saved.Events, e => e.EventType == "LagMarker");
        Assert.Contains(saved.Events, e => e.EventType == "GameProcess" && e.Description.Contains("no longer running"));
        Assert.Equal(4, saved.Connections.Count);
        Assert.All(saved.Connections, c => { Assert.Equal("Unknown", c.Role); Assert.NotNull(c.EndedUtc); });
        Assert.Contains(saved.Connections, c => c.Protocol == "UDP" && c.Remote == null);
        var proc = Assert.Single(saved.GameProcesses);
        Assert.Equal(6000, proc.Pid);
        Assert.NotNull(proc.ExitedUtc);
        Assert.Equal(2, saved.Targets.Count(t => t.Kind == MeasurementKind.GameProcessEndpoint));
        Assert.DoesNotContain(saved.Targets, t => t.Kind == MeasurementKind.GameServer);
        Assert.NotNull(saved.Findings);
    }

    [Fact]
    public void ProcessRestartDuringMonitoring_StartsANewConnectionSet()
    {
        M.Select("lol", out _);
        M.Start(out _);
        _procs.Current.Add(Game(6000));
        _conns.Current.Add(Tcp("34.1.2.3", 443, 6000));
        Poll(2);

        _procs.Current.Clear(); _conns.Current.Clear();
        Poll(1);
        _procs.Current.Add(new ProcessInfo(7000, "League of Legends", T0.AddMinutes(9), GamePath));
        _conns.Current.Add(Tcp("34.1.2.3", 443, 7000, local: 50999));
        Poll(2);

        var snap = M.Snapshot();
        Assert.Equal(7000, snap.Process!.Primary!.Pid);
        Assert.Equal(1, snap.Connections.Count(c => c.IsActive));
        Assert.Equal(2, snap.Connections.Count);
        var session = M.Stop(out _)!;
        Assert.Equal(2, _host.Sessions.Load(session.Id)!.GameProcesses.Count);
    }

    [Fact]
    public void DiscoveryWarnings_AreVisibleAndLoggedOnce()
    {
        M.Select("lol", out _);
        M.Start(out _);
        _procs.Current.Add(Game());
        _conns.Warnings.Add("IPv6 UDP table could not be read (Windows error 5).");
        Poll(3);

        Assert.Single(M.Snapshot().Warnings);
        Assert.Single(_host.Events.GetRecentEvents(100), e => e.Description.Contains("Connection discovery limited"));
    }

    [Fact]
    public void CannotChangeGameWhileMonitoring_AndCannotStartWithoutAGame()
    {
        Assert.False(M.Start(out var msg));
        Assert.Contains("Select a game", msg);

        M.Select("lol", out _);
        M.Start(out _);
        Assert.False(M.Select("fortnite", out var why));
        Assert.Contains("Stop monitoring", why);
        Assert.False(M.Start(out _));
        M.Stop(out _);
        Assert.True(M.Select("fortnite", out _));
    }

    [Fact]
    public void Start_IsRefusedWhileANetworkRecordingIsActive()
    {
        _host.StartSession();
        M.Select("lol", out _);
        Assert.False(M.Start(out var msg));
        Assert.Contains("already in progress", msg);
        _host.StopSession(out _);
    }

    [Fact]
    public void MeasuringRequiresMonitoring_AndStopMeasuringRemovesTheTarget()
    {
        M.Select("lol", out _);
        Assert.False(M.MeasureEndpoint("x", EndpointMethod.Icmp, out _));
        Assert.False(M.MarkLag());

        M.Start(out _);
        _procs.Current.Add(Game());
        _conns.Current.Add(Tcp("34.1.2.3", 443));
        Poll(2);
        var c = M.Snapshot().Connections.Single();
        Assert.True(M.MeasureEndpoint(c.Id, EndpointMethod.Icmp, out _));
        var targetId = M.Snapshot().Measured.Single().TargetId;

        Assert.True(M.StopMeasuring(targetId));
        Assert.Empty(_host.Engine.DynamicTargets);
        Assert.False(M.StopMeasuring(targetId));
    }

    [Fact]
    public void ShutdownWhileMonitoring_SavesTheSessionAndStopsCleanly()
    {
        string sessionId;
        using (var host = new NetPulseHost(_dir + "-shutdown", _procs, _conns))
        {
            host.GameMonitor.Select("lol", out _);
            host.GameMonitor.Start(out _);
            _procs.Current.Add(Game());
            _conns.Current.Add(Tcp("34.1.2.3", 443));
            host.GameMonitor.PollOnce();
            host.GameMonitor.PollOnce();
            host.GameMonitor.MarkLag("before shutdown");
            sessionId = host.Sessions.List().Single().Id; // checkpoint written at start
            // dispose with the monitor still running
        }

        var store = new SessionStore(Path.Combine(_dir + "-shutdown", "sessions"));
        var saved = store.Load(sessionId)!;
        Assert.True(saved.IsComplete);
        Assert.Single(saved.LagMarkers);
        Assert.Single(saved.Connections);
        Directory.Delete(_dir + "-shutdown", true);
    }

    [Fact]
    public void BackgroundLoop_DetectsTheGame_WithoutManualPolling_AndStopsOnDispose()
    {
        using var host = new NetPulseHost(_dir + "-loop", _procs, _conns);
        host.GameMonitor.IdlePollInterval = TimeSpan.FromMilliseconds(500);
        host.GameMonitor.Select("lol", out _);

        _procs.Current.Add(Game());
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && host.GameMonitor.Snapshot().Process?.State != GameRunState.Running) Thread.Sleep(100);
        Assert.Equal(GameRunState.Running, host.GameMonitor.Snapshot().Process?.State);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.GameMonitor.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {sw.Elapsed}");

        int calls = _conns.Captures;
        Thread.Sleep(800);
        Assert.Equal(calls, _conns.Captures); // loop really stopped
        if (Directory.Exists(_dir + "-loop")) Directory.Delete(_dir + "-loop", true);
    }

    [Fact]
    public void UpdatedEvent_IsRaisedForUiRefresh()
    {
        int raised = 0;
        M.Updated += (_, _) => raised++;
        M.Select("lol", out _);
        M.Start(out _);
        Poll();
        Assert.True(raised >= 3);
    }

    [Fact]
    public void MonitoringGap_IsRecordedAsAGapEvent_NotAsAnOutage()
    {
        M.Select("lol", out _);
        M.Start(out _);
        _host.Recorder.RecordCycle(DateTime.UtcNow.AddSeconds(1));
        _host.Recorder.RecordCycle(DateTime.UtcNow.AddMinutes(10)); // laptop slept

        var session = M.Stop(out _)!;
        Assert.Single(session.Gaps);
        Assert.Empty(session.Disconnects);
        Assert.Contains(session.Events, e => e.EventType == "MonitoringGap" && e.Description.Contains("not a network outage"));
    }
}
