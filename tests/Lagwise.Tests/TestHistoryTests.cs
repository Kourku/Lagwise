using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

/// <summary>Stored test results, ping during real heavy traffic, and this PC's traffic around lag markers (0.14).</summary>
public class TestHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "np-tests-" + Guid.NewGuid().ToString("N"));

    private static TestRecord Speed(DateTime t, double down, double up = 20, string connection = "Wired") =>
        new() { T = t, Kind = TestKind.Speed, DownMbps = down, UpMbps = up, Connection = connection };

    private static TestRecord Bloat(DateTime t, double delta, string grade = "B") =>
        new() { T = t, Kind = TestKind.Bufferbloat, DownDeltaMs = delta, UpDeltaMs = delta / 2, Grade = grade, RestMs = 12 };

    // ---- the store

    [Fact]
    public void Results_SurviveARestart_NewestFirst_AndClearOnlyForgetsOneKind()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "test-history.json");
            var h = new TestHistory(path);
            h.Add(Speed(T0, 300));
            h.Add(Speed(T0.AddHours(2), 250));
            h.Add(Bloat(T0.AddHours(1), 20));

            var again = new TestHistory(path);
            Assert.Equal(3, again.All().Count);
            Assert.Equal(250, again.Of(TestKind.Speed)[0].DownMbps);
            Assert.Equal(250, again.Latest(TestKind.Speed)!.DownMbps);
            Assert.Equal(2, again.Clear(TestKind.Speed));
            Assert.Single(new TestHistory(path).All());
            Assert.DoesNotContain("192.168", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void OnlyTheNewestResultsAreKept()
    {
        var h = new TestHistory();
        for (int i = 0; i < TestHistory.MaxRecords + 20; i++) h.Add(Speed(T0.AddMinutes(i), i));
        Assert.Equal(TestHistory.MaxRecords, h.All().Count);
        Assert.Equal(20, h.All().Min(r => r.DownMbps));
    }

    [Fact]
    public void AnUnreadableFile_IsSetAside_NotOverwritten()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "test-history.json");
            File.WriteAllText(path, "{ not json");
            var h = new TestHistory(path);
            Assert.Empty(h.All());
            Assert.True(File.Exists(path + ".unreadable"));
            h.Add(Speed(T0, 100));
            Assert.Single(new TestHistory(path).All());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheBufferbloatResultOfOlderVersions_StartsTheHistory_Once()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var old = new BufferbloatResult { Timestamp = T0, Grade = "C", BaselineLatencyMs = 15, DownloadDeltaMs = 45, UploadDeltaMs = 10, IsValid = true };
            File.WriteAllText(Path.Combine(dir, "last-bufferbloat.json"), System.Text.Json.JsonSerializer.Serialize(old));

            using (var host = new LagwiseHost(dir))
            {
                host.RememberBufferbloat(new BufferbloatResult { Timestamp = T0.AddDays(1), Grade = "A", BaselineLatencyMs = 14, DownloadDeltaMs = 6, IsValid = true });
                var tests = host.Tests.Of(TestKind.Bufferbloat);
                Assert.Equal(new[] { "A", "C" }, tests.Select(t => t.Grade));
            }
            using (var host = new LagwiseHost(dir))
                Assert.Equal(2, host.Tests.Of(TestKind.Bufferbloat).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void FailedTests_AreNotKept()
    {
        string dir = TempDir();
        try
        {
            using var host = new LagwiseHost(dir);
            host.RememberSpeedTest(new SpeedTestResult { CompletedSuccessfully = false, ErrorMessage = "offline" });
            host.RememberBufferbloat(new BufferbloatResult { IsValid = false });
            Assert.Empty(host.Tests.All());
            host.RememberSpeedTest(new SpeedTestResult { CompletedSuccessfully = true, DownloadSpeedMbps = 412.37, UploadSpeedMbps = 38.04, Timestamp = T0 });
            Assert.Equal(412.4, host.Tests.Latest(TestKind.Speed)!.DownMbps);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- what the results add up to (local time = UTC in these tests)

    private static DateTime At(int day, int hour) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SlowerEvenings_AreCalledOut()
    {
        var tests = new[] { Speed(At(10, 10), 400), Speed(At(11, 14), 420), Speed(At(10, 21), 210), Speed(At(11, 20), 230) };
        string s = TestTrends.Speed(tests, t => t);
        Assert.Contains("Typical of your 4 tests", s);
        Assert.Contains("Evening tests are 46% slower than daytime ones (220 against 410 Mbps down)", s);
    }

    [Fact]
    public void SimilarEvenings_AndTooFewTests_AreSaidPlainly()
    {
        var same = new[] { Speed(At(10, 10), 400), Speed(At(11, 14), 410), Speed(At(10, 21), 390), Speed(At(11, 20), 405) };
        Assert.Contains("about the same", TestTrends.Speed(same, t => t));
        Assert.StartsWith("One test so far", TestTrends.Speed(new[] { Speed(At(10, 10), 400) }, t => t));
        Assert.Contains("Test in the evening a couple of times too", TestTrends.Speed(new[] { Speed(At(10, 9), 400), Speed(At(10, 10), 400), Speed(At(10, 11), 400) }, t => t));
        Assert.Equal("", TestTrends.Speed(Array.Empty<TestRecord>(), t => t));
    }

    [Fact]
    public void WiFiAndCable_AreCompared_WhenBothWereTested()
    {
        var tests = new[] { Speed(At(10, 10), 400), Speed(At(10, 11), 120, connection: "Wi-Fi") };
        Assert.Contains("On Wi-Fi typically 120 Mbps down, on a cable 400 Mbps down.", TestTrends.Speed(tests, t => t));
    }

    [Fact]
    public void EveningsAndDaytime_AreComparedOnOneKindOfConnection()
    {
        // daytime Wi-Fi tests would make the daytime look slow; only the cable tests are compared
        var tests = new[]
        {
            Speed(At(10, 10), 400), Speed(At(11, 14), 410), Speed(At(10, 21), 405), Speed(At(11, 20), 395), Speed(At(12, 21), 400),
            Speed(At(12, 10), 90, connection: "Wi-Fi"), Speed(At(13, 11), 95, connection: "Wi-Fi")
        };
        string s = TestTrends.Speed(tests, t => t);
        Assert.Contains("Evening and daytime tests are about the same (cable tests only)", s);
        Assert.DoesNotContain("slower than", s);
    }

    [Fact]
    public void BufferbloatTrend_ComparesTheRiseUnderLoad()
    {
        var tests = new[] { Bloat(At(10, 10), 8, "A"), Bloat(At(11, 11), 10, "A"), Bloat(At(10, 21), 45, "C"), Bloat(At(11, 22), 55, "C") };
        string s = TestTrends.Bufferbloat(tests, t => t);
        Assert.Contains("recent grades: A, C, A, C", s);
        Assert.Contains("Evenings show more lag under load (+50 ms against +9 ms in daytime)", s);
    }

    // ---- ping during real heavy traffic

    private static Measurement Ping(string id, double sec, double ms, bool ok = true) =>
        new() { TargetId = id, Timestamp = T0.AddSeconds(sec), RttMs = ms, Success = ok, Protocol = NetworkProtocol.Icmp };

    private static readonly IReadOnlySet<string> Refs = new HashSet<string> { "near", "far" };

    [Fact]
    public void PingUnderLoad_ComparesEachServerWithItself()
    {
        var history = new List<Measurement>();
        for (int s = -50; s < 0; s += 2) { history.Add(Ping("near", s, 10)); history.Add(Ping("far", s, 150)); history.Add(Ping("router", s, 1)); }
        for (int s = 0; s <= 60; s += 2) { history.Add(Ping("near", s, 50)); history.Add(Ping("far", s, 190, ok: s % 10 != 0)); history.Add(Ping("router", s, 30)); }

        var before = PingUnderLoad.Before(history, Refs, T0);
        Assert.Equal(new[] { "far", "near" }, before.Keys.OrderBy(k => k));
        var p = PingUnderLoad.During(history, Refs, before, T0, T0.AddSeconds(60))!;
        Assert.Equal(40, p.RiseMs);        // both servers rose by 40 ms; their distance does not matter
        Assert.Equal(2, p.Servers);
        Assert.Equal(80, p.RestMs);        // median of 10 and 150
        Assert.True(p.LossPercent > 5);
    }

    [Fact]
    public void PingUnderLoad_NeedsRepliesOnBothSides_AndRespectsAnEarlierStretch()
    {
        var history = new List<Measurement> { Ping("near", -10, 10), Ping("near", -8, 10), Ping("near", 5, 40) };
        Assert.Empty(PingUnderLoad.Before(history, Refs, T0));
        Assert.Null(PingUnderLoad.During(history, Refs, new Dictionary<string, double>(), T0, T0.AddSeconds(30)));

        var busy = Enumerable.Range(0, 20).Select(i => Ping("near", -40 + i, 60)).ToList();   // an earlier stretch, still loaded
        Assert.Empty(PingUnderLoad.Before(busy, Refs, T0, notBeforeUtc: T0.AddSeconds(-15)));
    }

    [Fact]
    public void PingUnderLoad_SaysWhatTheRiseMeans()
    {
        var h = new HeavyTrafficStretch("{eth}", "Ethernet", T0, T0.AddSeconds(130), 90e6, 4e6, 62e6, 3e6);
        Assert.Contains("ended after 2 min 10 s (about 62 Mbps down and 3 Mbps up on average)", PingUnderLoad.Describe(h, null));
        Assert.Contains("too few pings", PingUnderLoad.Describe(h, null));
        Assert.Contains("stayed steady meanwhile (+3 ms: 14 ms before, 17 ms during)", PingUnderLoad.Describe(h, new LoadPing(14, 17, 3, 0, 2)));
        Assert.Contains("line may not have been full", PingUnderLoad.Describe(h, new LoadPing(14, 17, 3, 0, 2)));
        Assert.Contains("rose a little", PingUnderLoad.Describe(h, new LoadPing(14, 36, 22, 0, 2)));
        string bad = PingUnderLoad.Describe(h, new LoadPing(14, 88, 74, 12, 2));
        Assert.Contains("rose by 74 ms meanwhile (14 ms before, 88 ms during): the load made your connection queue (bufferbloat)", bad);
        Assert.Contains("12% of the pings during it got no reply", bad);
    }

    // ---- stretches of heavy traffic

    private sealed class Fake
    {
        public long Rx;
        public IReadOnlyList<AdapterCounters> Read() => new[] { new AdapterCounters("{eth}", "Ethernet", "Test NIC", "Ethernet", 1_000_000_000, Rx, 0, 0, 0, 0, 0) };
    }

    [Fact]
    public void AShortDip_DoesNotEndAStretch_AndTheEndIsReportedOnce()
    {
        var f = new Fake();
        var m = new AdapterActivityMonitor(f.Read);
        var started = new List<HeavyTraffic>();
        var ended = new List<HeavyTrafficStretch>();
        m.HeavyTrafficDetected += (_, h) => started.Add(h);
        m.HeavyTrafficEnded += (_, h) => ended.Add(h);
        int s = 0;
        void Run(int seconds, long bytesPerSecond) { for (int i = 0; i < seconds; i++) { f.Rx += bytesPerSecond; m.Sample(T0.AddSeconds(++s)); } }
        m.Sample(T0);

        Run(8, 5_000_000);      // 40 Mbps
        Run(3, 10_000);         // a launcher writing to disk
        Run(8, 10_000_000);     // 80 Mbps: the same stretch, now long enough
        Assert.Single(started);
        Assert.Equal(T0.AddSeconds(1), started[0].StartUtc);
        Assert.Equal("{eth}", started[0].AdapterId);
        Assert.Empty(ended);

        Run(10, 10_000);        // over
        var e = Assert.Single(ended);
        Assert.Equal(T0.AddSeconds(1), e.StartUtc);
        Assert.Equal(T0.AddSeconds(19), e.EndUtc);
        Assert.Equal(80e6, e.PeakRxBps, 3);
        Assert.Equal(60e6, e.AverageRxBps, 3);   // the heavy seconds only: 8 at 40 and 8 at 80
    }

    [Fact]
    public void ShortBursts_AndLagwisesOwnTests_HaveNoEnd()
    {
        var f = new Fake();
        bool testing = false;
        var m = new AdapterActivityMonitor(f.Read) { SuppressHeavyTraffic = () => testing };
        int ended = 0;
        m.HeavyTrafficEnded += (_, _) => ended++;
        int s = 0;
        void Run(int seconds, long bytesPerSecond) { for (int i = 0; i < seconds; i++) { f.Rx += bytesPerSecond; m.Sample(T0.AddSeconds(++s)); } }
        m.Sample(T0);

        Run(6, 5_000_000); Run(10, 10_000);              // too short to be reported, so no end either
        Run(15, 5_000_000); testing = true; Run(10, 5_000_000); testing = false; Run(10, 10_000);   // spoilt by a speed test
        Assert.Equal(0, ended);
    }

    // ---- lag markers and this PC's own traffic

    private static MonitoringSession Recording(params (int Sec, double Rx)[] traffic)
    {
        var s = new MonitoringSession { StartedUtc = T0, EndedUtc = T0.AddMinutes(10), IsComplete = true };
        s.Targets.Add(new SessionTarget { Id = "cf", Name = "Cloudflare", Protocol = NetworkProtocol.Icmp, Kind = MeasurementKind.ReferenceEndpoint });
        for (int i = 0; i < 600; i += 2) s.Samples.Add(new SessionSample { T = T0.AddSeconds(i), TargetId = "cf", Ok = true, RttMs = 12 });
        foreach (var (sec, rx) in traffic) s.Throughput.Add(new ThroughputSample { T = T0.AddSeconds(sec), Rx = rx, Tx = 1e6 });
        return s;
    }

    [Fact]
    public void LagMarkers_SayWhetherThisPCWasBusy()
    {
        var traffic = Enumerable.Range(0, 600).Select(i => (i, i is >= 95 and <= 110 ? 48e6 : 0.5e6)).ToArray();
        var s = Recording(traffic);
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(100) });
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(400) });

        var stats = SessionAnalysis.Compute(s);
        Assert.True(stats.LagCorrelations[0].Traffic!.Busy);
        Assert.False(stats.LagCorrelations[1].Traffic!.Busy);
        Assert.Contains("This PC was busy: up to 48 Mbps down", stats.LagCorrelations[0].Traffic!.Describe());
        Assert.Contains("light", stats.LagCorrelations[1].Traffic!.Describe());
        Assert.StartsWith("1 of the 2 moments you marked came while this PC was busy", stats.LagTrafficSummary);
        Assert.Contains("This PC was busy", SessionReportBuilder.BuildMarkdown(s, stats));

        // a higher limit from Settings: 48 Mbps is no longer "busy"
        Assert.Equal(0, SessionAnalysis.Compute(s, busyBps: 100e6).LagWhileBusy);
    }

    [Fact]
    public void OlderRecordings_WithoutTraffic_SayNothingAboutIt()
    {
        var s = Recording();
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(100) });
        var stats = SessionAnalysis.Compute(s);
        Assert.Null(stats.LagCorrelations[0].Traffic);
        Assert.Equal("", stats.LagTrafficSummary);
    }

    // ---- the connection report

    [Fact]
    public async Task TheReport_ListsTestsOverTime_InPlainWords_WithoutTheWiFiName()
    {
        var nic = new NetworkInterfaceInfo { Id = "{w}", Name = "Wi-Fi", IsWifi = true, WifiSsid = "HomeNet-5G", WifiBssid = "aa:bb:cc:dd:ee:ff", WifiSignalPercent = 71, WifiChannel = 36 };
        var events = new List<ConnectionEvent> { new() { Timestamp = At(10, 21), EventType = "HeavyTrafficEnded", Severity = "Warning", Description = "Ping rose." } };
        var tests = new[] { Speed(At(10, 10), 400), Speed(At(10, 21), 210, connection: "Wi-Fi"), Seen(45, At(10, 21)) };

        string md = await new StorageService().GenerateDiagnosticReportAsync(nic, new DiagnosticResult { Headline = "Mostly steady." }, Array.Empty<TargetMetrics>(), events,
            tests: tests, toLocal: t => t);

        Assert.Contains("## Tests Over Time", md);
        Assert.Contains("| 2026-10-10 21:00 | Speed test | Wi-Fi | 210.0 Mbps down, 20.0 Mbps up |", md);
        Assert.Contains("Seen in real use", md);
        Assert.Contains("`2026-10-10 21:00:00` **Warning**, Heavy traffic ended: Ping rose.", md);
        Assert.Contains("signal 71%", md);
        Assert.DoesNotContain("HomeNet", md);
        Assert.DoesNotContain("aa:bb:cc", md);
        Assert.Contains("not game servers", md);
    }

    // ---- the checklist

    private static TargetMetrics Gw() => new() { TargetId = "gateway", IsGateway = true, Protocol = NetworkProtocol.Icmp, MedianLatency = 1, CurrentLatency = 1, RecentSampleCount = 60, TotalProbes = 60 };

    private static ReadinessItem Load(BufferbloatResult? b, TestRecord? seen) =>
        ReadinessCheck.Evaluate(new[] { Gw() }, null, b, T0, seen).Items.Single(i => i.Id == "load");

    private static TestRecord Seen(double rise, DateTime t) => new() { T = t, Kind = TestKind.RealLoad, RiseMs = rise, RestMs = 14, LoadedMs = 14 + rise, DownMbps = 80 };

    [Fact]
    public void Checklist_UsesRealUse_HonestlyWhenThereIsNoTest()
    {
        var clear = Load(null, Seen(45, T0.AddHours(-3)));
        Assert.Equal(CheckStatus.Fair, clear.Status);
        Assert.Contains("Seen in real use 3 h ago: ping rose by 45 ms while this PC downloaded at about 80 Mbps", clear.Detail);

        var steady = Load(null, Seen(4, T0.AddHours(-3)));
        Assert.Equal(CheckStatus.Unknown, steady.Status);    // a steady ping at part load proves little
        Assert.Contains("may not have been full", steady.Detail);

        Assert.Equal(CheckStatus.Unknown, Load(null, Seen(90, T0.AddDays(-20))).Status);   // too old to matter
        Assert.Equal(CheckStatus.Poor, Load(null, Seen(90, T0.AddHours(-1))).Status);
    }

    [Fact]
    public void Checklist_KeepsTheTestGrade_ButMentionsWorseRealUse()
    {
        var test = new BufferbloatResult { Timestamp = T0.AddDays(-2), Grade = "A", DownloadDeltaMs = 8, IsValid = true };
        Assert.Equal(CheckStatus.Good, Load(test, Seen(10, T0.AddHours(-1))).Status);
        var worse = Load(test, Seen(70, T0.AddHours(-1)));
        Assert.Equal(CheckStatus.Fair, worse.Status);
        Assert.Contains("Bufferbloat grade A", worse.Detail);
        Assert.Contains("ping rose by 70 ms", worse.Detail);
    }
}
