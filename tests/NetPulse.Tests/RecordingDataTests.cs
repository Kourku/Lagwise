using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

/// <summary>What a recording keeps about this PC and its game, and how automatic recordings are kept in check.</summary>
public class RecordingDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpulse-recdata-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(500, 1000, 0, 50.0)]     // kernel time includes idle time
    [InlineData(1000, 1000, 0, 0.0)]
    [InlineData(0, 400, 600, 100.0)]
    [InlineData(250, 500, 500, 75.0)]
    public void ProcessorLoad_IsTheBusyShareOfTheTimeThatPassed(long idle, long kernel, long user, double expected) =>
        Assert.Equal(expected, ProcessorLoad.Percent(idle, kernel, user));

    [Fact]
    public void ProcessorLoad_WithNoTimePassed_IsUnknown() => Assert.Null(ProcessorLoad.Percent(0, 0, 0));

    [Fact]
    public void ProcessorLoad_ReadsWindowsCounters()
    {
        if (!OperatingSystem.IsWindows()) return;
        var load = new ProcessorLoad();
        Assert.Null(load.Sample());                // the first reading only primes the counters
        var end = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < end) { }          // keep one core busy for a moment
        var v = load.Sample();
        Assert.NotNull(v);
        Assert.InRange(v!.Value, 0, 100);
    }

    [Fact]
    public void Recorder_KeepsProcessorAndWifi_WithEachSecond_AndSnapshotsCarryThem()
    {
        var r = new SessionRecorder();
        r.Start(null, null, Array.Empty<TargetConfig>(), null, 1000);
        r.RecordThroughput(DateTime.UtcNow, 1_000_000, 50_000, 123.4, 140);
        r.RecordThroughput(DateTime.UtcNow, 2_000_000, 60_000);

        var snap = r.Snapshot()!;
        Assert.Equal(2, snap.Throughput.Count);
        Assert.Equal(100, snap.Throughput[0].Cpu);        // clamped
        Assert.Equal(100, snap.Throughput[0].Wifi);
        Assert.Null(snap.Throughput[1].Cpu);
        Assert.Equal(NetPulse.Core.AppInfo.Version, snap.AppVersion);
    }

    [Fact]
    public void Recorder_NamesAWaitingRecordingAfterItsGame_AndKeepsTheFirstGame()
    {
        var r = new SessionRecorder();
        r.Start(null, $"{SessionRecorder.WaitingName} 2026-10-20 20:05", Array.Empty<TargetConfig>(), null, 1000);
        r.MarkAutomatic();
        r.SetGameContext(GameProfiles.Find("cs2")!);
        r.SetGameContext(GameProfiles.Find("lol")!);      // another game later does not take over

        var s = r.Stop()!;
        Assert.Equal("cs2", s.GameId);
        Assert.Equal("Counter-Strike 2", s.GameDisplayName);
        Assert.Equal("Counter-Strike 2 2026-10-20 20:05", s.Name);
        Assert.True(s.Automatic);
    }

    private MonitoringSession Saved(SessionStore store, int minutesAgo, bool automatic, int lagMarks = 0, bool complete = true)
    {
        var s = new MonitoringSession { StartedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo), EndedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo + 1), IsComplete = complete, Automatic = automatic };
        for (int i = 0; i < lagMarks; i++) s.LagMarkers.Add(new LagMarker { T = s.StartedUtc });
        store.Save(s);
        return s;
    }

    [Fact]
    public void AutomaticRecordings_ArePrunedToTheNewest_KeepingMarkedAndManualOnes()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessions"));
        var newest = Saved(store, 1, true);
        var second = Saved(store, 2, true);
        var old = Saved(store, 3, true);
        var oldWithLag = Saved(store, 4, true, lagMarks: 1);
        var manual = Saved(store, 5, false);
        var interrupted = Saved(store, 6, true, complete: false);

        Assert.Equal(1, store.PruneAutomatic(2));
        var left = store.List().Select(x => x.Id).ToHashSet();
        Assert.Contains(newest.Id, left);
        Assert.Contains(second.Id, left);
        Assert.DoesNotContain(old.Id, left);
        Assert.Contains(oldWithLag.Id, left);
        Assert.Contains(manual.Id, left);
        Assert.Contains(interrupted.Id, left);
        Assert.Equal(0, store.PruneAutomatic(0));          // 0 keeps all
    }

    [Fact]
    public void AnOlderIndex_IsWorkedOutAgain_SoRecordingsGetTheirVerdict()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessions"));
        var s = Saved(store, 10, false);
        Assert.NotNull(store.List().Single().Verdict);

        // an index written by 0.x has no version and no verdicts: it must not be trusted
        string index = Path.Combine(_dir, "sessions", "index.json");
        var json = File.ReadAllText(index).Replace("\"Version\":3,", "").Replace("\"Verdict\":\"NotEnoughData\",", "");
        File.WriteAllText(index, json);
        Assert.NotNull(store.List().Single(x => x.Id == s.Id).Verdict);
    }

    [Fact]
    public void SettingsFrom0x_WatchAllGames_AndRecordByThemselves_Once()
    {
        string dir = Path.Combine(_dir, "upgrade");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, """{ "SchemaVersion": 1, "LastGameId": "lol", "AutoRecordGame": false, "MonitoringIntervalMs": 2000 }""");

        var service = new SettingsService(file);
        var s = service.Load();
        Assert.Equal(GameMonitorService.AllGames, s.LastGameId);
        Assert.True(s.AutoRecordGame);
        Assert.Equal(2000, s.MonitoringIntervalMs);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);

        // once: a choice made in 1.0 stays
        s.AutoRecordGame = false;
        s.LastGameId = "lol";
        Assert.True(service.TrySave(out _));
        var again = new SettingsService(file).Load();
        Assert.False(again.AutoRecordGame);
        Assert.Equal("lol", again.LastGameId);
    }

    [Fact]
    public void Settings_KeepOnlyUsableAddedGames()
    {
        var a = new AppSettings
        {
            UserGames = new List<string> { @"D:\Games\a.exe", @"d:\games\A.EXE", "relative.exe", @"D:\Games\notes.txt" },
            IgnoredGames = new List<string> { "win:1", "", "win:1" },
            AutoRecordingsKept = -5
        };
        a.Normalize();
        Assert.Equal(new[] { @"D:\Games\a.exe" }, a.UserGames);
        Assert.Equal(new[] { "win:1" }, a.IgnoredGames);
        Assert.Equal(0, a.AutoRecordingsKept);
        Assert.True(new AppSettings().AutoRecordGame);
    }
}
