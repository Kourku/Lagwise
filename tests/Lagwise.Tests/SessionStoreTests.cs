using Lagwise.Core.Models;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lagwise-store-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static MonitoringSession Make(DateTime start, bool complete = true, int samples = 3, int lag = 0)
    {
        var s = new MonitoringSession
        {
            Name = "Test " + start.ToString("HH:mm"),
            StartedUtc = start,
            EndedUtc = complete ? start.AddMinutes(10) : null,
            IsComplete = complete,
            AppVersion = "0.4.0",
            GameId = "lol",
            GameDisplayName = "League of Legends"
        };
        for (int i = 0; i < samples; i++) s.Samples.Add(new SessionSample { T = start.AddSeconds(i), TargetId = "a", Ok = true, RttMs = 10 });
        for (int i = 0; i < lag; i++) s.LagMarkers.Add(new LagMarker { T = start.AddSeconds(i) });
        return s;
    }

    [Fact]
    public void List_ReturnsSummariesNewestFirst_WithLagCountsSizeAndVersion()
    {
        var store = new SessionStore(_dir);
        var older = Make(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var newer = Make(new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc), lag: 2);
        store.Save(older);
        store.Save(newer);

        var list = store.List();
        Assert.Equal(new[] { newer.Id, older.Id }, list.Select(s => s.Id));
        Assert.Equal(2, list[0].LagMarkerCount);
        Assert.Equal("0.4.0", list[0].AppVersion);
        Assert.Equal("League of Legends", list[0].GameDisplayName);
        Assert.True(list[0].SizeBytes > 0);
        Assert.True(File.Exists(Path.Combine(_dir, "index.json")));
    }

    [Fact]
    public void List_PicksUpChangedAndDeletedFiles_EvenWithAnIndex()
    {
        var store = new SessionStore(_dir);
        var a = Make(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var b = Make(new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        store.Save(a);
        store.Save(b);
        Assert.Equal(2, store.List().Count);

        // a checkpoint rewrites the file: the index entry must not be trusted any more
        for (int i = 0; i < 50; i++) a.Samples.Add(new SessionSample { T = a.StartedUtc.AddMinutes(1).AddSeconds(i), TargetId = "a", Ok = true, RttMs = 12 });
        store.Save(a);
        store.Delete(b.Id);

        var list = store.List();
        Assert.Single(list);
        Assert.Equal(53, list[0].SampleCount);
    }

    [Fact]
    public void List_SurvivesADamagedIndexAndAnUnreadableRecording()
    {
        var store = new SessionStore(_dir);
        store.Save(Make(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        File.WriteAllText(Path.Combine(_dir, "index.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "deadbeef.session.json"), "garbage");

        Assert.Single(store.List());
    }

    [Fact]
    public void DeleteOlderThan_RemovesOldCompleteRecordings_ButKeepsInterruptedOnes()
    {
        var store = new SessionStore(_dir);
        var old = Make(new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var oldInterrupted = Make(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc), complete: false);
        var recent = Make(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        store.Save(old);
        store.Save(oldInterrupted);
        store.Save(recent);

        int deleted = store.DeleteOlderThan(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { recent.Id, oldInterrupted.Id }.OrderBy(x => x), store.List().Select(s => s.Id).OrderBy(x => x));
    }

    [Fact]
    public void BuildLive_ChartsOnlyTheRequestedWindowOfLiveHistory()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var history = new List<Measurement>();
        for (int i = 0; i < 600; i++)
            history.Add(new Measurement { Timestamp = now.AddSeconds(-600 + i), TargetId = "cf", Success = true, RttMs = i < 400 ? 500 : 20 });

        var chart = SessionChartBuilder.BuildLive(history, new[] { new SessionTarget { Id = "cf", Name = "Cloudflare" } }, now, TimeSpan.FromMinutes(2), ChartMode.Latency);

        Assert.Equal(120, chart.DurationSeconds); // more history than the window exists, so the full window is shown
        var series = Assert.Single(chart.Series);
        Assert.All(series.Points, p => Assert.Equal(20, p.Y)); // the 500 ms samples are older than the window
    }

    [Fact]
    public void BuildLive_ShowsOnlyTheHistoryThatExists_RightAfterStartUp()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var history = Enumerable.Range(0, 90).Select(i => new Measurement { Timestamp = now.AddSeconds(-90 + i), TargetId = "cf", Success = true, RttMs = 20 }).ToList();

        var chart = SessionChartBuilder.BuildLive(history, new[] { new SessionTarget { Id = "cf", Name = "Cloudflare" } }, now, TimeSpan.FromMinutes(15), ChartMode.Latency);

        Assert.InRange(chart.DurationSeconds, 89, 91);
    }
}
