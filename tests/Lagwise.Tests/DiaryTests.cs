using Lagwise.Core.Games;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

/// <summary>Outages beyond the router, and the connection diary (0.15).</summary>
public class DiaryTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc);

    // ---- "Connection lost" now covers a break beyond the router

    private sealed class Rig
    {
        public EventLogService Events { get; } = new();
        public MeasurementEngine Engine { get; }

        public Rig()
        {
            var settings = new AppSettings
            {
                RecentWindowSeconds = 10,
                Targets = new List<TargetConfig>
                {
                    new() { Id = "gw", Name = "Gateway", Host = "192.168.0.1", IsGateway = true },
                    new() { Id = "dns", Name = "DNS", Host = "example.test", Protocol = NetworkProtocol.Dns, IsDnsResolver = true },
                    new() { Id = "a", Name = "Server A", Host = "203.0.113.1" },
                    new() { Id = "b", Name = "Server B", Host = "203.0.113.2" }
                }
            };
            Engine = new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(),
                new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), Events) { Settings = settings };
        }

        private static Measurement M(string id, int s, bool ok) => new()
        {
            TargetId = id, TargetName = id, TargetHost = id, Timestamp = T0.AddSeconds(s), Success = ok, RttMs = ok ? 20 : 0,
            Protocol = id == "dns" ? NetworkProtocol.Dns : NetworkProtocol.Icmp
        };

        public void Cycle(int s, bool router = true, bool dns = true, bool a = true, bool b = true) =>
            Engine.IngestCycle(new[] { M("gw", s, router), M("dns", s, dns), M("a", s, a), M("b", s, b) }, T0.AddSeconds(s));

        public IReadOnlyList<ConnectionEvent> Log(string type) => Events.GetRecentEvents(1000).Where(e => e.EventType == type).ToList();
    }

    [Fact]
    public void TheInternetGoingSilent_WhileTheRouterAnswers_IsAConnectionLoss()
    {
        var rig = new Rig();
        rig.Cycle(0);
        for (int s = 1; s <= 2; s++) rig.Cycle(s, a: false, b: false);   // the router and the DNS cache still answer
        Assert.Empty(rig.Log("Disconnected"));
        rig.Cycle(3, a: false, b: false);

        var down = Assert.Single(rig.Log("Disconnected"));
        Assert.Equal(MeasurementEngine.BeyondRouterContext, down.ContextData);
        Assert.Contains("beyond your home network", down.Description);
        Assert.Equal(T0.AddSeconds(1), down.Timestamp);
        Assert.True(rig.Engine.IsInternetDown);

        rig.Cycle(4, b: false);   // one server answers again: back online
        var back = Assert.Single(rig.Log("Restored"), e => e.TargetName == SessionRecorder.ConnectivityScope);
        Assert.StartsWith("Back online", back.Description);
        Assert.False(rig.Engine.IsInternetDown);
    }

    [Fact]
    public void NothingAnswering_NotEvenTheRouter_IsALocalLoss()
    {
        var rig = new Rig();
        rig.Cycle(0);
        for (int s = 1; s <= 3; s++) rig.Cycle(s, false, false, false, false);
        Assert.Equal(MeasurementEngine.LocalContext, Assert.Single(rig.Log("Disconnected")).ContextData);
    }

    [Fact]
    public void ServersThatNeverAnswered_AreNotAnOutage()
    {
        // a network that blocks pings: the router answers, the servers never have
        var rig = new Rig();
        for (int s = 0; s < 10; s++) rig.Cycle(s, a: false, b: false);
        Assert.Empty(rig.Log("Disconnected"));
    }

    [Fact]
    public void OneServerDown_IsNotAnOutage()
    {
        var rig = new Rig();
        for (int s = 0; s < 10; s++) rig.Cycle(s, a: false);
        Assert.Empty(rig.Log("Disconnected"));
    }

    [Fact]
    public void TheAlert_SaysWhereTheBreakIs()
    {
        var p = new AlertPolicy(T0);
        var e = new ConnectionEvent { EventType = "Disconnected", Timestamp = T0.AddMinutes(5), TargetName = SessionRecorder.ConnectivityScope, ContextData = MeasurementEngine.BeyondRouterContext };
        Assert.Contains("modem or your provider", p.Evaluate(e)!.Message);
    }

    // ---- the diary

    private static void Hour(ConnectionDiary d, DateTime start, double rtt = 20, double jitter = 0, int failEvery = 0, int seconds = 3600, string key = "Icmp|1.1.1.1")
    {
        for (int s = 0; s < seconds; s++)
        {
            var t = start.AddSeconds(s);
            bool ok = failEvery == 0 || s % failEvery != 0;
            d.AddProbe(t, key, "Cloudflare", ok, rtt + (s % 2 == 0 ? jitter : 0));
            d.AddCycle(t, 1000);
        }
    }

    [Fact]
    public void AnHour_KeepsMeasuredTime_Ping_Variation_AndProbes()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, rtt: 20, jitter: 4, failEvery: 50);
        d.AddCycle(T0.AddHours(1).AddSeconds(1), 1000);   // closes the hour

        var h = d.Snapshot().Hours.First(x => x.StartUtc == T0);
        Assert.InRange(h.MonitoredSeconds, 3590, 3600);
        var s = Assert.Single(h.Servers);
        Assert.Equal(3600, s.Probes);
        Assert.Equal(3528, s.Replies);
        Assert.InRange(s.VariationMs, 3.5, 4.1);
        Assert.InRange(s.MedianMs, 20, 24);
    }

    [Fact]
    public void ALongPause_IsNotMeasuredTime()
    {
        var d = new ConnectionDiary();
        d.AddCycle(T0, 1000);
        d.AddCycle(T0.AddSeconds(1), 1000);
        d.AddCycle(T0.AddMinutes(30), 1000);   // the PC slept
        d.AddCycle(T0.AddMinutes(30).AddSeconds(1), 1000);
        Assert.Equal(2, d.Snapshot().Hours.Single().MonitoredSeconds, 3);
    }

    [Fact]
    public void Drops_AreKept_AndTheirProbesAreNotCountedAsLoss()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, seconds: 600);
        d.OutageStarted(T0.AddSeconds(600), beyondRouter: true);
        for (int s = 600; s < 640; s++) { d.AddProbe(T0.AddSeconds(s), "Icmp|1.1.1.1", "Cloudflare", false, 0); d.AddCycle(T0.AddSeconds(s), 1000); }
        d.OutageEnded(T0.AddSeconds(640));

        var snap = d.Snapshot();
        var drop = Assert.Single(snap.Outages);
        Assert.True(drop.BeyondRouter);
        Assert.Equal(T0.AddSeconds(640), drop.EndUtc);
        Assert.Equal(600, snap.Hours.Single().Servers.Single().Probes);   // the 40 failed probes during the drop are the drop, not loss

        var view = DiaryAnalysis.Build(snap, 7, T0.AddHours(1), t => t);
        Assert.Equal(40, view.OfflineSeconds, 3);
        var cell = view.Days[0].Hours[20]!;
        Assert.Equal(1, cell.Drops);
        Assert.Equal(DiaryLevel.Poor, cell.LevelFor(DiaryMetric.Overall));
        Assert.Contains("1 drop without internet, 40 s in total (beyond your router, at your modem or provider)", view.Summary);
    }

    [Fact]
    public void TheDiary_SurvivesARestart_AndADropOpenAtCloseEndsUnseen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-diary-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "diary.json");
            var d = new ConnectionDiary(path);
            Hour(d, T0, seconds: 1200);
            d.OutageStarted(T0.AddSeconds(1000), beyondRouter: false);
            d.Save();

            var again = new ConnectionDiary(path);
            var snap = again.Snapshot();
            Assert.InRange(snap.Hours.Single().MonitoredSeconds, 1190, 1200);
            var drop = Assert.Single(snap.Outages);
            Assert.True(drop.EndedUnseen);
            Assert.Equal(T0.AddSeconds(1199), drop.EndUtc);

            // the same hour continues after the restart: both parts add up
            Hour(again, T0.AddSeconds(1800), seconds: 600);
            again.AddCycle(T0.AddHours(1).AddSeconds(1), 1000);
            var merged = again.Snapshot().Hours.First(h => h.StartUtc == T0);
            Assert.InRange(merged.MonitoredSeconds, 1780, 1800);
            Assert.Equal(1800, merged.Servers.Single().Probes);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PingAboveUsual_ComparesEachServerWithItself()
    {
        var d = new ConnectionDiary();
        for (int h = 0; h < 6; h++)
        {
            Hour(d, T0.AddHours(h), rtt: 20, key: "Icmp|near");
            Hour(d, T0.AddHours(h), rtt: 150, key: "Icmp|far");
        }
        Hour(d, T0.AddHours(6), rtt: 45, key: "Icmp|near");    // the evening slows down by 25 ms...
        Hour(d, T0.AddHours(6), rtt: 175, key: "Icmp|far");    // ...on both servers
        d.AddCycle(T0.AddHours(7).AddSeconds(1), 1000);

        var view = DiaryAnalysis.Build(d.Snapshot(), 7, T0.AddHours(7), t => t);
        var normal = view.Days.SelectMany(x => x.Hours).First(c => c != null && c.LocalStart == T0)!;
        var slow = view.Days.SelectMany(x => x.Hours).First(c => c != null && c.LocalStart == T0.AddHours(6))!;
        Assert.Equal(0, normal.PingAboveUsualMs!.Value, 1);   // a far server is not "slow", it is far
        Assert.Equal(25, slow.PingAboveUsualMs!.Value, 1);
        Assert.Equal(DiaryLevel.Fair, slow.LevelFor(DiaryMetric.PingAboveUsual));
        Assert.Equal(DiaryLevel.Good, normal.LevelFor(DiaryMetric.Overall));
        Assert.Same(slow, view.WorstPing);
    }

    [Fact]
    public void TheGrid_HasOneRowPerDay_NewestFirst_AndLeavesUnmeasuredHoursEmpty()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, seconds: 120);   // 2 min: shown, but too little to rate
        var view = DiaryAnalysis.Build(d.Snapshot(), 14, T0.AddDays(2), t => t);
        Assert.Equal(14, view.Days.Count);
        Assert.Equal(T0.AddDays(2).Date, view.Days[0].LocalDate);
        var cell = view.Days[2].Hours[20]!;
        Assert.Equal(DiaryLevel.Thin, cell.LevelFor(DiaryMetric.Overall));
        Assert.Null(view.Days[2].Hours[19]);
        Assert.Contains("Too little measured", cell.Describe());
    }

    [Fact]
    public void AServerThatNeverAnswers_IsFilteringPings_NotLosingThem()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, key: "Icmp|ok", failEvery: 100);
        for (int s = 0; s < 3600; s++) d.AddProbe(T0.AddSeconds(s), "Icmp|silent", "Silent", false, 0);
        var view = DiaryAnalysis.Build(d.Snapshot(), 1, T0.AddMinutes(59), t => t);
        Assert.Equal(1, view.LossPercent!.Value, 1);
    }

    [Fact]
    public void AnEmptyDiary_SaysHowItFillsIn()
    {
        var view = DiaryAnalysis.Build(new ConnectionDiary().Snapshot(), 14, T0, t => t);
        Assert.True(view.IsEmpty);
        Assert.Contains("fills in by itself", view.Summary);
    }

    [Fact]
    public async Task TheConnectionReport_CarriesTheDiary_AndTheSiteCheck()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, seconds: 900);
        d.OutageStarted(T0.AddSeconds(900), beyondRouter: true);
        d.OutageEnded(T0.AddSeconds(960));
        var view = DiaryAnalysis.Build(d.Snapshot(), 7, T0.AddHours(1), t => t);
        Assert.Equal("Last 7 days (Lagwise measured 15 min): 1 drop (1 min 00 s without internet), probe loss 0%.", DiaryAnalysis.SummaryLine(view, 7));

        var site = Lagwise.Core.Sites.SiteCatalog.Popular[0];
        var result = new Lagwise.Core.Sites.SiteResult(site, Lagwise.Core.Sites.SiteVerdict.DnsBlocked, "Blocked by your DNS", "Your DNS sends www.youtube.com to 0.0.0.0.", "", Array.Empty<Lagwise.Core.Sites.SiteStep>(), T0);
        string md = await new StorageService().GenerateDiagnosticReportAsync(new NetworkInterfaceInfo(), new DiagnosticResult(), Array.Empty<TargetMetrics>(), new List<ConnectionEvent>(),
            toLocal: t => t, diaryLines: DiaryAnalysis.ReportLines(view, t => t), sites: new[] { result });
        Assert.Contains("## Connection Diary (last 14 days)", md);
        Assert.Contains("| Wed 2026-10-14 | 15 min | 1 |", md);
        Assert.Contains("## Site Check", md);
        Assert.Contains("| YouTube (www.youtube.com) | Blocked by your DNS | Your DNS sends www.youtube.com to 0.0.0.0. |", md);

        var summary = StatusSummary.Build(new ReadinessResult(CheckStatus.Good, "Good to play", Array.Empty<ReadinessItem>()), Array.Empty<TargetMetrics>(), null, T0, "0.15.0", "Last 7 days: no drops.");
        Assert.Contains("Last 7 days: no drops.", summary);
        Assert.EndsWith("it does not measure game-server ping.", summary);
    }

    [Fact]
    public void TheReport_ListsDaysAndDrops()
    {
        var d = new ConnectionDiary();
        Hour(d, T0, seconds: 900);
        d.OutageStarted(T0.AddSeconds(900), beyondRouter: true);
        d.OutageEnded(T0.AddSeconds(960));
        var lines = DiaryAnalysis.ReportLines(DiaryAnalysis.Build(d.Snapshot(), 7, T0.AddHours(1), t => t), t => t);
        Assert.Contains(lines, l => l.StartsWith("| Wed 2026-10-14 | 15 min | 1 | 1 min 00 s |"));
        Assert.Contains(lines, l => l.StartsWith("- 2026-10-14 20:15:00: 1 min 00 s. Beyond your router"));
    }
}

/// <summary>The "Ready to play?" notice when the selected game starts (0.15).</summary>
public class GameStartCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lagwise-gamestart-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessSource _procs = new();
    private readonly LagwiseHost _host;

    public GameStartCheckTests() => _host = new LagwiseHost(_dir, _procs, new FakeConnectionSource());

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ProcessInfo Game() => new(6000, "League of Legends", DateTime.UtcNow, @"E:\Riot Games\League of Legends\Game\League of Legends.exe");

    [Fact]
    public void AGameThatStarts_IsAnnounced_ButNotOneAlreadyRunning()
    {
        var launched = new List<string>();
        _host.GameMonitor.GameLaunched += (_, g) => launched.Add(g.Id);

        lock (_procs.Current) _procs.Current.Add(Game());   // already running when watching begins
        _host.GameMonitor.Select("lol", out _);
        _host.GameMonitor.PollOnce();
        _host.GameMonitor.PollOnce();
        Assert.Empty(launched);

        lock (_procs.Current) _procs.Current.Clear();
        _host.GameMonitor.PollOnce();
        lock (_procs.Current) _procs.Current.Add(Game() with { Pid = 6100 });
        _host.GameMonitor.PollOnce();
        Assert.Equal(new[] { "lol" }, launched);
    }

    [Fact]
    public void TheNotice_SaysWhatToWatch()
    {
        var items = new List<ReadinessItem>
        {
            new("router", "Your router", CheckStatus.Good, "Quick."),
            new("loss", "Packet loss", CheckStatus.Poor, "2 of 3 public servers are losing test messages."),
            new("stability", "Ping stability", CheckStatus.Fair, "Ping swings by about 12 ms between replies.")
        };
        var (title, message, problem) = ReadinessCheck.GameStartNotice("League of Legends", new ReadinessResult(CheckStatus.Poor, "Expect lag", items));
        Assert.Equal("League of Legends started: expect lag", title);
        Assert.StartsWith("Packet loss: 2 of 3", message);
        Assert.Contains("Ping stability", message);
        Assert.True(problem);

        var good = ReadinessCheck.GameStartNotice("Fortnite", new ReadinessResult(CheckStatus.Good, "Good", items.Take(1).ToList()));
        Assert.Equal("Fortnite started: good to play", good.Title);
        Assert.False(good.Problem);
    }
}
