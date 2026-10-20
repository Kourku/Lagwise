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

public sealed class FakeGameList : IGameListSource
{
    public List<WindowsGameEntry> Entries { get; } = new();
    public IReadOnlyList<WindowsGameEntry> Read() => Entries.ToList();
}

public class GameMonitorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string LeaguePath = @"C:\Games\Riot Games\League of Legends\Game\League of Legends.exe";
    private const string OtherPath = @"D:\Library\Space Rally\Binaries\Win64\SpaceRally-Win64-Shipping.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpulse-monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessSource _procs = new();
    private readonly FakeConnectionSource _conns = new();
    private readonly NetPulseHost _host;

    /// <summary>A game Windows lists on the test PC (any game, not a built-in one).</summary>
    private static readonly GameProfile Rally = GameLibrary.Build(Array.Empty<GameProfile>(), Array.Empty<WindowsGameEntry>(), new[] { OtherPath },
        exists: _ => true, versionInfo: _ => ("Space Rally", null)).Single();

    public GameMonitorTests()
    {
        _host = new NetPulseHost(_dir, _procs, _conns, gameList: new FakeGameList());
        _host.Settings.AutoRecordGame = false;   // tests that want it switch it on
        M.SetLibrary(GameProfiles.All.Append(Rally).ToList());
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ProcessInfo League(int pid = 6000) => new(pid, "League of Legends", T0.AddMinutes(1), LeaguePath);
    private static ProcessInfo LeagueClient() => new(5000, "LeagueClient", T0, @"C:\Games\Riot Games\League of Legends\LeagueClient.exe");
    private static ProcessInfo RallyProcess(int pid = 8000) => new(pid, "SpaceRally-Win64-Shipping", T0.AddMinutes(2), OtherPath);

    private static RawConnection Tcp(string ip, int port, int pid = 6000, int local = 50000) =>
        new(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.20"), local, IPAddress.Parse(ip), port, ConnectionState.Established, pid);

    private static RawConnection Udp(int port, int pid = 6000) =>
        new(TransportProtocol.Udp, IPAddress.Any, port, null, 0, ConnectionState.NotApplicable, pid);

    private GameMonitorService M => _host.GameMonitor;

    private void Poll(int times = 1) { for (int i = 0; i < times; i++) M.PollOnce(); }

    [Fact]
    public void WatchingOneGame_ReportsItsClientHonestly_AndReadsNoSockets()
    {
        Assert.True(M.Select("lol", out _));
        _procs.Current.Add(LeagueClient());
        Poll();

        var snap = M.Snapshot();
        Assert.Equal("lol", snap.Game!.Id);
        Assert.Equal(GameRunState.LauncherOnly, snap.Process!.State);
        Assert.False(snap.IsMonitoring);
        Assert.Equal(0, _conns.Captures); // no socket reading unless a recorded game runs
    }

    [Fact]
    public void WatchingAllGames_FindsWhicheverGameRuns_BuiltInOrNot()
    {
        Assert.True(M.Select(GameMonitorService.AllGames, out var msg));
        Assert.Contains("all your games", msg);
        Poll();
        Assert.Null(M.Snapshot().Game);
        Assert.Empty(M.Snapshot().Running);

        _procs.Current.Add(RallyProcess());
        Poll();
        var snap = M.Snapshot();
        Assert.Equal(Rally.Id, snap.Game!.Id);
        Assert.Equal(GameRunState.Running, snap.Process!.State);
        Assert.Equal("Space Rally", Assert.Single(snap.Running).DisplayName);
    }

    [Fact]
    public void FullWorkflow_RecordsTheGame_ItsSockets_LagMarks_AndSaves()
    {
        M.Select("lol", out _);
        Assert.True(M.Start(out _));
        Assert.True(_host.Recorder.IsRecording);

        // recording started before the game: nothing to read yet
        Poll();
        Assert.Equal(0, _conns.Captures);

        _procs.Current.AddRange(new[] { LeagueClient(), League() });
        _conns.Current.AddRange(new[] { Tcp("34.1.2.3", 443), Tcp("52.9.9.9", 8393, local: 50001), Udp(7777), Tcp("127.0.0.1", 2999, local: 50002) });
        Poll(2);

        var snap = M.Snapshot();
        Assert.Equal(GameRunState.Running, snap.Process!.State);
        Assert.Equal(1, snap.UdpSockets);
        Assert.Equal(2, snap.InternetConnections);          // loopback and the UDP socket are not internet connections
        Assert.Contains(_host.Events.GetRecentEvents(100), e => e.EventType == "GameProcess" && e.Description.Contains("started"));

        Assert.True(M.MarkLag("rubber banding"));
        _procs.Current.Remove(_procs.Current.Single(p => p.Name == "League of Legends"));
        _conns.Current.Clear();
        Poll(1);
        Assert.Equal(GameRunState.LauncherOnly, M.Snapshot().Process!.State);

        var session = M.Stop(out var error)!;
        Assert.Null(error);
        Assert.False(M.IsMonitoring);
        Assert.False(_host.Recorder.IsRecording);

        var saved = _host.Sessions.Load(session.Id)!;
        Assert.True(saved.IsComplete);
        Assert.False(saved.Automatic);
        Assert.Equal("lol", saved.GameId);
        Assert.Equal("League of Legends", saved.GameDisplayName);
        Assert.Equal("rubber banding", Assert.Single(saved.LagMarkers).Note);
        Assert.Contains(saved.Events, e => e.EventType == "GameProcess" && e.Description.Contains("was closed"));
        Assert.Equal(4, saved.Connections.Count);
        Assert.All(saved.Connections, c => Assert.NotNull(c.EndedUtc));
        Assert.Contains(saved.Connections, c => c.Protocol == "UDP" && c.Remote == null);
        Assert.NotNull(Assert.Single(saved.GameProcesses).ExitedUtc);
        Assert.DoesNotContain(saved.Targets, t => t.Kind is MeasurementKind.GameServer or MeasurementKind.GameProcessEndpoint);
    }

    [Fact]
    public void ARecordingStartedBeforeAnyGame_TakesTheFirstGameThatStarts()
    {
        M.Select(GameMonitorService.AllGames, out _);
        Assert.True(M.Start(out var msg));
        Assert.Contains("whenever you are ready", msg);
        Assert.Null(M.RecordedGame);
        Assert.StartsWith(SessionRecorder.WaitingName, _host.Recorder.Snapshot()!.Name);

        _procs.Current.Add(RallyProcess());
        Poll();
        Assert.Equal(Rally.Id, M.RecordedGame!.Id);

        // a second game starting later does not take the recording over
        _procs.Current.Add(League());
        Poll();
        Assert.Equal(Rally.Id, M.RecordedGame!.Id);

        var saved = M.Stop(out _)!;
        Assert.Equal(Rally.Id, saved.GameId);
        Assert.Equal("Space Rally", saved.GameDisplayName);
        Assert.StartsWith("Space Rally ", saved.Name);
    }

    [Fact]
    public void AutomaticRecording_WithAllGames_StartsForAnyGame_AndStopsWhenItCloses()
    {
        var now = T0;
        using var host = new NetPulseHost(_dir + "-auto", _procs, _conns, gameList: new FakeGameList());
        var saved = new List<MonitoringSession>();
        host.SessionSaved += (_, e) => saved.Add(e.Session);
        var monitor = host.GameMonitor;
        Assert.True(host.Settings.AutoRecordGame);          // on unless the user turned it off
        monitor.SetLibrary(GameProfiles.All.Append(Rally).ToList());
        monitor.Select(GameMonitorService.AllGames, out _);

        monitor.PollOnce();
        Assert.False(monitor.IsMonitoring);

        _procs.Current.Add(RallyProcess());
        monitor.PollOnce();
        Assert.True(monitor.IsMonitoring);
        Assert.True(monitor.StartedAutomatically);
        Assert.Equal(Rally.Id, monitor.RecordedGame!.Id);

        _procs.Current.Clear();
        monitor.AutoStopGrace = TimeSpan.Zero;
        monitor.PollOnce();
        Assert.False(monitor.IsMonitoring);
        var session = Assert.Single(saved);
        Assert.True(session.Automatic);
        Assert.Equal("Space Rally", session.GameDisplayName);
        host.Dispose();
        Directory.Delete(_dir + "-auto", true);
    }

    [Fact]
    public void StoppingByHand_WhileTheGameRuns_IsNotUndoneByAutomaticRecording_UntilTheGameClosesAndStartsAgain()
    {
        _host.Settings.AutoRecordGame = true;
        M.Select(GameMonitorService.AllGames, out _);
        _procs.Current.Add(RallyProcess());
        Poll();
        Assert.True(M.IsMonitoring);

        M.Stop(out _);
        Poll(3);
        Assert.False(M.IsMonitoring);                 // the player stopped it: no new recording two seconds later
        Assert.Equal(Rally.Id, M.NotRecordingAgain);

        _procs.Current.Clear();
        Poll();
        Assert.Null(M.NotRecordingAgain);
        _procs.Current.Add(RallyProcess(8100));
        Poll();
        Assert.True(M.IsMonitoring);                  // the next time it starts, it is recorded again
        M.Stop(out _);
    }

    [Fact]
    public void GameLaunched_IsRaisedForAGameThatStartsWhileWatched_NotForOneAlreadyRunning()
    {
        var launched = new List<string>();
        M.GameLaunched += (_, p) => launched.Add(p.Id);
        _procs.Current.Add(League());
        M.Select(GameMonitorService.AllGames, out _);
        Poll();
        Assert.Empty(launched);                 // already running when watching began

        _procs.Current.Add(RallyProcess());
        Poll();
        Assert.Equal(new[] { Rally.Id }, launched);
    }

    [Fact]
    public void NewLibrary_KeepsWhatIsRunning_WithoutAFalseStart()
    {
        var launched = 0;
        M.GameLaunched += (_, _) => launched++;
        M.Select(GameMonitorService.AllGames, out _);
        Poll();
        _procs.Current.Add(League());
        Poll(2);
        Assert.Equal(1, launched);

        M.SetLibrary(GameProfiles.All.Append(Rally).ToList());   // found again (start-up refresh, a game added)
        Poll();
        Assert.Equal(1, launched);
        Assert.Equal("lol", M.Snapshot().Game!.Id);
    }

    [Fact]
    public void ConnectionReadingProblems_AreNotedOnce()
    {
        M.Select("lol", out _);
        M.Start(out _);
        _procs.Current.Add(League());
        _conns.Warnings.Add("IPv6 UDP table could not be read (Windows error 5).");
        Poll(3);

        Assert.Single(_host.Events.GetRecentEvents(100), e => e.Description.Contains("could not be read"));
    }

    [Fact]
    public void CannotChangeGameWhileRecording_AndCannotStartWithNothingWatched()
    {
        Assert.False(M.Start(out var msg));
        Assert.Contains("Choose a game", msg);

        M.Select("lol", out _);
        M.Start(out _);
        Assert.False(M.Select("fortnite", out var why));
        Assert.Contains("Stop recording", why);
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
        Assert.Contains("already running", msg);
        Assert.Contains("History & reports", msg);
        _host.StopSession(out _);
    }

    [Fact]
    public void MarkLag_NeedsARecording()
    {
        M.Select("lol", out _);
        Assert.False(M.MarkLag());
        M.Start(out _);
        Assert.True(M.MarkLag());
        M.Stop(out _);
    }

    [Fact]
    public void ShutdownWhileRecording_SavesTheSessionAndStopsCleanly()
    {
        string sessionId;
        using (var host = new NetPulseHost(_dir + "-shutdown", _procs, _conns, gameList: new FakeGameList()))
        {
            host.Settings.AutoRecordGame = false;
            host.GameMonitor.Select("lol", out _);
            host.GameMonitor.Start(out _);
            _procs.Current.Add(League());
            _conns.Current.Add(Tcp("34.1.2.3", 443));
            host.GameMonitor.PollOnce();
            host.GameMonitor.PollOnce();
            host.GameMonitor.MarkLag("before shutdown");
            sessionId = host.Sessions.List().Single().Id; // checkpoint written at start
            // dispose with the monitor still recording
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
        using var host = new NetPulseHost(_dir + "-loop", _procs, _conns, gameList: new FakeGameList());
        host.Settings.AutoRecordGame = false;
        host.GameMonitor.IdlePollInterval = TimeSpan.FromMilliseconds(500);
        host.GameMonitor.Select(GameMonitorService.AllGames, out _);

        _procs.Current.Add(League());
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && host.GameMonitor.Snapshot().Process?.State != GameRunState.Running) Thread.Sleep(100);
        Assert.Equal(GameRunState.Running, host.GameMonitor.Snapshot().Process?.State);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.GameMonitor.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {sw.Elapsed}");
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

    [Fact]
    public void Host_FindsGamesFromWindowsList_AddedGames_AndHonoursRemovals()
    {
        string exe = Path.Combine(_dir, "games", "Neon Drift", "NeonDrift.exe");
        string added = Path.Combine(_dir, "games", "Indie", "indie.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(added)!);
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        File.WriteAllBytes(added, new byte[] { 0x4D, 0x5A });

        var list = new FakeGameList();
        list.Entries.Add(new WindowsGameEntry(exe, T0, "123"));
        using var host = new NetPulseHost(_dir + "-lib", _procs, _conns, gameList: list);
        Assert.True(host.AddGame(added, out var err), err);
        var library = host.RefreshGames();

        Assert.Contains(library, g => g.Source == GameSource.Windows && g.DisplayName == "Neon Drift");
        var mine = Assert.Single(library, g => g.Source == GameSource.AddedByYou);
        Assert.Equal("Indie", mine.DisplayName);
        Assert.Equal(library.Count, host.GameMonitor.Library.Count);

        Assert.True(host.RemoveGame(library.Single(g => g.Source == GameSource.Windows), out _));
        Assert.True(host.RemoveGame(mine, out _));
        var after = host.RefreshGames();
        Assert.DoesNotContain(after, g => g.Source != GameSource.BuiltIn);
        Assert.Empty(host.Settings.UserGames);

        Assert.False(host.AddGame(Path.Combine(_dir, "missing.exe"), out var why));
        Assert.Contains(".exe", why);
        Assert.True(host.RestoreGames(out _));
        Assert.Contains(host.RefreshGames(), g => g.DisplayName == "Neon Drift");
        host.Dispose();
        Directory.Delete(_dir + "-lib", true);
    }
}
