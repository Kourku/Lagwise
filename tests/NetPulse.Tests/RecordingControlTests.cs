using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

/// <summary>One recorder is shared by the Game Monitor and plain network recordings; these tests pin down how they interact.</summary>
public class RecordingControlTests : IDisposable
{
    private const string GamePath = @"E:\Riot Games\League of Legends\Game\League of Legends.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpulse-recording-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessSource _procs = new();
    private readonly FakeConnectionSource _conns = new();
    private readonly NetPulseHost _host;
    private readonly List<SessionSavedEventArgs> _saved = new();

    public RecordingControlTests()
    {
        _host = new NetPulseHost(_dir, _procs, _conns);
        _host.SessionSaved += (_, e) => _saved.Add(e);
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ProcessInfo Game() => new(6000, "League of Legends", DateTime.UtcNow.AddMinutes(-1), GamePath);

    [Fact]
    public void PlainRecording_CannotStartWhileTheGameMonitorRecords_InsteadOfCrashing()
    {
        _host.GameMonitor.Select("lol", out _);
        Assert.True(_host.GameMonitor.Start(out _));

        Assert.False(_host.TryStartSession(out var error));
        Assert.Contains("Game Monitor", error);
        Assert.True(_host.GameMonitor.IsMonitoring); // the game recording is untouched
    }

    [Fact]
    public void MarkLag_GoesToWhicheverRecordingRuns_AndIsRefusedWhenNothingRecords()
    {
        Assert.False(_host.MarkLag());

        Assert.True(_host.TryStartSession(out _));
        Assert.True(_host.MarkLag("stutter"));
        var plain = _host.StopSession(out _)!;
        Assert.Single(plain.LagMarkers);
        Assert.Contains(plain.Events, e => e.EventType == "LagMarker");

        _host.GameMonitor.Select("lol", out _);
        _host.GameMonitor.Start(out _);
        Assert.True(_host.MarkLag());
        var game = _host.GameMonitor.Stop(out _)!;
        Assert.Single(game.LagMarkers);
    }

    [Fact]
    public void Stopping_RaisesSessionSaved_ForPlainAndGameRecordings()
    {
        _host.TryStartSession(out _);
        _host.StopSession(out _);
        _host.GameMonitor.Select("lol", out _);
        _host.GameMonitor.Start(out _);
        _host.GameMonitor.Stop(out _);

        Assert.Equal(2, _saved.Count);
        Assert.All(_saved, e => Assert.Null(e.Error));
        Assert.Null(_saved[0].Session.GameId);
        Assert.Equal("lol", _saved[1].Session.GameId);
        Assert.All(_saved, e => Assert.Equal(NetPulse.Core.AppInfo.Version, e.Session.AppVersion));
    }

    [Fact]
    public void AutoRecord_StartsWhenTheGameStarts_AndSavesOnceItHasBeenClosedForTheGracePeriod()
    {
        _host.Settings.AutoRecordGame = true;
        var m = _host.GameMonitor;
        m.AutoStopGrace = TimeSpan.FromMilliseconds(300);
        m.Select("lol", out _);

        m.PollOnce();
        Assert.False(m.IsMonitoring); // the game is not running yet

        _procs.Current.Add(Game());
        m.PollOnce();
        Assert.True(m.IsMonitoring);
        Assert.True(m.StartedAutomatically);

        _procs.Current.Clear();
        m.PollOnce();
        Assert.True(m.IsMonitoring); // closed only just now: a quick restart must not split the recording

        _procs.Current.Add(Game());
        m.PollOnce();
        _procs.Current.Clear();
        m.PollOnce();
        Thread.Sleep(400);
        m.PollOnce();

        Assert.False(m.IsMonitoring);
        var saved = Assert.Single(_saved);
        Assert.Equal("lol", saved.Session.GameId);
        Assert.Contains(saved.Session.Events, e => e.Description.Contains("automatically"));
    }

    [Fact]
    public void AutoRecord_IsOffByDefault_AndNeverStopsAManualRecording()
    {
        Assert.False(_host.Settings.AutoRecordGame);
        var m = _host.GameMonitor;
        m.AutoStopGrace = TimeSpan.Zero;
        m.Select("lol", out _);
        _procs.Current.Add(Game());
        m.PollOnce();
        Assert.False(m.IsMonitoring);

        m.Start(out _);
        _procs.Current.Clear();
        m.PollOnce();
        m.PollOnce();
        Assert.True(m.IsMonitoring);
        Assert.False(m.StartedAutomatically);
    }

    [Fact]
    public void AutoRecord_DoesNotInterruptAPlainRecording()
    {
        _host.Settings.AutoRecordGame = true;
        _host.TryStartSession(out _);
        _host.GameMonitor.Select("lol", out _);
        _procs.Current.Add(Game());
        _host.GameMonitor.PollOnce();

        Assert.False(_host.GameMonitor.IsMonitoring);
        Assert.True(_host.Recorder.IsRecording);
    }
}
