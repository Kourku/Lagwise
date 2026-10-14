using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Statistics;

namespace NetPulse.Tests;

public class PolishTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);

    // ---- long-term "usual" latency ----

    [Fact]
    public void BaselineMemory_NeedsAFewValues_ThenUsesTheirMedian()
    {
        var b = new BaselineMemory();
        string key = BaselineMemory.KeyOf(NetworkProtocol.Icmp, "1.1.1.1");
        b.Add(key, 30, T0);
        b.Add(key, 31, T0.AddMinutes(10));
        Assert.Null(b.Usual(key));
        b.Add(key, 90, T0.AddMinutes(20));   // one slow evening sample does not move the median much
        Assert.Equal(31, b.Usual(key));
    }

    [Fact]
    public void BaselineMemory_KeepsOnlyRecentValues()
    {
        var b = new BaselineMemory();
        string key = "Icmp|x";
        for (int i = 0; i < BaselineMemory.MaxValues + 10; i++) b.Add(key, i < 10 ? 500 : 20, T0.AddMinutes(10 * i));
        Assert.Equal(BaselineMemory.MaxValues, b.Count(key));
        Assert.Equal(20, b.Usual(key));   // the oldest (slow) values were dropped

        b.Add(key, 25, T0.AddDays(60));   // everything else is older than MaxAge
        Assert.Equal(1, b.Count(key));
        Assert.Null(b.Usual(key));
    }

    [Fact]
    public void BaselineMemory_IgnoresNonsense_AndSurvivesARestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-baseline-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "baselines.json");
        try
        {
            var b = new BaselineMemory(path);
            b.Add("k", 0, T0);
            b.Add("k", double.NaN, T0);
            Assert.Equal(0, b.Count("k"));
            for (int i = 0; i < 3; i++) b.Add("k", 12, T0.AddMinutes(10 * i));
            b.Save();

            var again = new BaselineMemory(path);
            Assert.Equal(12, again.Usual("k"));

            File.WriteAllText(path, "{ not json");
            Assert.Null(new BaselineMemory(path).Usual("k"));   // a damaged file means learning again, not a crash
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static MeasurementEngine Engine(BaselineMemory? memory, EventLogService? events = null)
    {
        var settings = new AppSettings
        {
            Targets = new List<TargetConfig>
            {
                new() { Id = "ref", Name = "Reference", Host = "203.0.113.1" },
                new() { Id = "web", Name = "HTTPS", Host = "example.test", Protocol = NetworkProtocol.Https }
            },
            RecentWindowSeconds = 60
        };
        return new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(),
            new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), events ?? new EventLogService())
        { Settings = settings, Baselines = memory };
    }

    private static Measurement M(string id, int second, double rtt, NetworkProtocol protocol = NetworkProtocol.Icmp) => new()
    {
        TargetId = id, TargetName = id, TargetHost = id == "ref" ? "203.0.113.1" : "example.test", Protocol = protocol,
        Timestamp = T0.AddSeconds(second), Success = true, RttMs = rtt
    };

    [Fact]
    public void SlowPingAfterARestart_IsHigherThanUsual_WhenTheNormalIsRemembered()
    {
        var memory = new BaselineMemory();
        for (int i = 0; i < 5; i++) memory.Add(BaselineMemory.KeyOf(NetworkProtocol.Icmp, "203.0.113.1"), 30, T0.AddHours(-i));

        var engine = Engine(memory);
        for (int s = 0; s < 40; s++) engine.IngestCycle(new[] { M("ref", s, 95) });
        var m = engine.GetCurrentMetrics().Single(x => x.TargetId == "ref");
        Assert.Equal(30, m.BaselineLatencyMs);
        Assert.True(m.IsLatencyElevated);

        // without the memory the slow window itself would be "usual"
        var forgetful = Engine(null);
        for (int s = 0; s < 40; s++) forgetful.IngestCycle(new[] { M("ref", s, 95) });
        Assert.False(forgetful.GetCurrentMetrics().Single(x => x.TargetId == "ref").IsLatencyElevated);
    }

    [Fact]
    public void SampleBaselines_StoresTargetsWithEnoughReplies()
    {
        var memory = new BaselineMemory();
        var engine = Engine(memory);
        for (int s = 0; s < 30; s++) engine.IngestCycle(new[] { M("ref", s, 22) });
        engine.SampleBaselines(T0.AddMinutes(10));
        Assert.Equal(1, memory.Count(BaselineMemory.KeyOf(NetworkProtocol.Icmp, "203.0.113.1")));
        Assert.Equal(0, memory.Count(BaselineMemory.KeyOf(NetworkProtocol.Https, "example.test")));   // never measured
    }

    // ---- spike events only for real round trips ----

    [Fact]
    public void HttpsTimingSwings_AreNotLoggedAsLatencySpikes()
    {
        var events = new EventLogService();
        var engine = Engine(null, events);
        engine.IngestCycle(new[] { M("web", 0, 110, NetworkProtocol.Https), M("ref", 0, 20) });
        engine.IngestCycle(new[] { M("web", 1, 420, NetworkProtocol.Https), M("ref", 1, 20) });
        Assert.DoesNotContain(events.GetRecentEvents(100), e => e.EventType == "LatencySpike");

        engine.IngestCycle(new[] { M("ref", 2, 260) });
        Assert.Contains(events.GetRecentEvents(100), e => e.EventType == "LatencySpike" && e.TargetName == "ref");
    }

    // ---- cached DNS is not "0 ms" ----

    [Fact]
    public void CachedLookups_AreDescribedAsCached()
    {
        var b = new HttpsTimingBreakdown { DnsLookupMs = 0.1, TcpConnectMs = 31, TlsHandshakeMs = 32, TtfbMs = 36 };
        Assert.Equal("DNS cached · TCP 31 · TLS 32 · first byte 36 ms", b.ShortText);
        b.DnsLookupMs = 14.4;
        Assert.StartsWith("DNS 14 ·", b.ShortText);

        var dns = new TargetMetrics { TargetId = "dns", TargetName = "DNS", IsDnsResolver = true, MedianLatency = 0.4, RecentSampleCount = 30, TotalProbes = 30 };
        var r = ReadinessCheck.Evaluate(new[] { dns }, new NetworkInterfaceInfo(), null, T0);
        Assert.Contains("answered from Windows' cache", r.Items.Single(i => i.Id == "dns").Detail);
    }
}

public class LoadTestWordingTests
{
    [Theory]
    [InlineData(2, "A+", "Ping stayed steady")]
    [InlineData(12, "A", "rose a little")]
    [InlineData(25, "B", "rose noticeably")]
    [InlineData(45, "C", "bufferbloat")]
    [InlineData(120, "D", "severe bufferbloat")]
    public void BufferbloatResult_SaysWhatLoadItWasMeasuredUnder(double delta, string grade, string summary)
    {
        var r = new BufferbloatResult { DownloadDeltaMs = delta, UploadDeltaMs = 1, DownloadLoadMbps = 412.4, UploadLoadMbps = 38.6 };
        r.Grade = BufferbloatResult.CalculateGrade(Math.Max(r.DownloadDeltaMs, r.UploadDeltaMs));
        BufferbloatService.FormatBufferbloatExplanation(r);

        Assert.Equal(grade, r.Grade);
        Assert.Contains(summary, r.Summary);
        Assert.Contains("downloaded at about 412 Mbps and uploaded at about 39 Mbps", r.Explanation);
        Assert.DoesNotContain("queue management", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BufferbloatPhases_AreLongEnoughToFillAConnection()
    {
        var s = new BufferbloatService(new PingService());
        Assert.True(s.PhaseMs >= 10_000);
        Assert.InRange(s.RampUpMs, 1_000, s.PhaseMs / 2);
    }
}
