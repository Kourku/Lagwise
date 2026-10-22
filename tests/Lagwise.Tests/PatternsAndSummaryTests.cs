using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class PatternsAndSummaryTests
{
    // local time == UTC in these tests, so hours are easy to read
    private static ConnectionPatterns NewPatterns() => new(t => t);

    /// <summary>A recording with one probe per second to each reference server, starting at the given hour.</summary>
    private static MonitoringSession Recording(int day, int hour, int minutes, double extraMs = 0, double lossEvery = 0, int lagMarks = 0)
    {
        var s = new MonitoringSession { Id = Guid.NewGuid().ToString("N"), StartedUtc = new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Utc) };
        s.Targets.Add(new SessionTarget { Id = "cf", Name = "Cloudflare", Host = "1.1.1.1", Kind = MeasurementKind.ReferenceEndpoint });
        s.Targets.Add(new SessionTarget { Id = "far", Name = "Far away", Host = "9.9.9.9", Kind = MeasurementKind.ReferenceEndpoint });
        s.Targets.Add(new SessionTarget { Id = "gw", Name = "Router", Host = "192.168.1.1", Kind = MeasurementKind.LocalGateway });
        int n = 0;
        for (int i = 0; i < minutes * 60; i++)
        {
            var t = s.StartedUtc.AddSeconds(i);
            foreach (var (id, baseMs) in new[] { ("cf", 12.0), ("far", 120.0), ("gw", 1.0) })
            {
                n++;
                bool lost = lossEvery > 0 && id != "gw" && n % (int)lossEvery == 0;
                s.Samples.Add(new SessionSample { T = t, TargetId = id, Ok = !lost, RttMs = lost ? null : baseMs + (id == "gw" ? 0 : extraMs) });
            }
        }
        for (int i = 0; i < lagMarks; i++) s.LagMarkers.Add(new LagMarker { T = s.StartedUtc.AddMinutes(1 + i) });
        return s;
    }

    [Fact]
    public void NoRecordings_SaysSo()
    {
        var r = NewPatterns().Build();
        Assert.Equal(24, r.Hours.Count);
        Assert.Null(r.WorstHour);
        Assert.Contains("No recordings", r.Summary);
    }

    [Fact]
    public void ASingleHour_IsNotEnoughToCompare()
    {
        var p = NewPatterns();
        p.Add(Recording(1, 20, 10));
        var r = p.Build();
        Assert.Equal(1, r.HoursWithData);
        Assert.Null(r.WorstHour);
        Assert.Contains("Not enough data", r.Summary);
    }

    [Fact]
    public void EveningSlowdown_IsFound_AndComparedPerServer()
    {
        var p = NewPatterns();
        for (int d = 1; d <= 3; d++)
        {
            p.Add(Recording(d, 14, 10));
            p.Add(Recording(d, 21, 10, extraMs: 18, lagMarks: 1));
        }
        var r = p.Build();
        Assert.Equal(21, r.WorstHour!.Hour);
        Assert.Equal(14, r.BestHour!.Hour);
        // each server is compared with itself: +9 ms at 21:00, -9 ms at 14:00 around the pooled median
        Assert.InRange(r.Hours[21].DeltaMs!.Value, 8, 19);
        Assert.True(r.Hours[14].DeltaMs < r.Hours[21].DeltaMs);
        Assert.Equal(3, r.Hours[21].LagMarkers);
        Assert.Equal(3, r.Hours[21].RecordingCount);
        Assert.Contains("worst around 21:00", r.Summary);
        Assert.Contains("evening", r.Summary);
    }

    [Fact]
    public void RouterSamples_AreIgnored()
    {
        var p = NewPatterns();
        p.Add(Recording(1, 10, 5));
        var r = p.Build();
        Assert.Equal(5 * 60 * 2, r.SampleCount);  // two reference servers, not the router
    }

    [Fact]
    public void ProbeLoss_AloneCanMakeAnHourTheWorst()
    {
        var p = NewPatterns();
        p.Add(Recording(1, 9, 10));
        p.Add(Recording(1, 23, 10, lossEvery: 20));
        var r = p.Build();
        Assert.Equal(23, r.WorstHour!.Hour);
        Assert.InRange(r.Hours[23].LossPercent, 4, 8);
        Assert.Contains("probe loss", r.Summary);
        Assert.Contains("one-off", r.Summary);   // a single recording covers that hour
    }

    [Fact]
    public void EvenHours_NameNoWorstHour()
    {
        var p = NewPatterns();
        p.Add(Recording(1, 8, 10, extraMs: 1));
        p.Add(Recording(1, 19, 10));
        var r = p.Build();
        Assert.Null(r.WorstHour);
        Assert.Contains("No hour stands out", r.Summary);
    }

    // ---- copy summary ----

    [Fact]
    public void Summary_HasVerdictAndNumbers_ButNoAddressesOrNetworkNames()
    {
        var nic = new NetworkInterfaceInfo
        {
            Name = "Wi-Fi 2", InterfaceType = "Wireless80211", OperationalStatus = "Up", IsWifi = true, WifiSsid = "SecretHomeNet",
            WifiSignalPercent = 71, Ipv4Address = "192.168.1.30", Ipv4Gateway = "192.168.1.1"
        };
        var metrics = new List<TargetMetrics>
        {
            new() { TargetId = "gateway", TargetName = "Router", TargetHost = "192.168.1.1", IsGateway = true, MedianLatency = 2.4, Jitter = 0.8, TotalProbes = 60, RecentSampleCount = 60 },
            new() { TargetId = "cf", TargetName = "Cloudflare DNS", TargetHost = "1.1.1.1", Protocol = NetworkProtocol.Icmp, CurrentLatency = 14, BaselineLatencyMs = 13, MedianLatency = 14, Jitter = 1.1, TotalProbes = 60, RecentSampleCount = 60 },
            new() { TargetId = "q9", TargetName = "Quad9", TargetHost = "9.9.9.9", Protocol = NetworkProtocol.Icmp, TotalProbes = 10, FailedProbes = 10, RecentSampleCount = 10 }
        };
        var readiness = ReadinessCheck.Evaluate(metrics, nic, null, DateTime.UtcNow);
        string text = StatusSummary.Build(readiness, metrics, nic, new DateTime(2026, 10, 14, 21, 5, 0), "0.9.0");

        Assert.StartsWith("Lagwise 0.9.0 connection check, 14 Oct 2026 21:05", text);
        Assert.Contains("Verdict: ", text);
        Assert.Contains("Wi-Fi, signal 71%", text);
        Assert.Contains("Router: 2.4 ms typical", text);
        Assert.Contains("- Cloudflare DNS: 14 ms now (usual 13)", text);
        Assert.Contains("- Quad9: no replies", text);
        Assert.Contains("does not measure game-server ping", text);
        Assert.DoesNotContain("192.168.", text);
        Assert.DoesNotContain("1.1.1.1", text);
        Assert.DoesNotContain("SecretHomeNet", text);
    }

    [Fact]
    public void Summary_WithoutConnection_SaysSo()
    {
        var readiness = ReadinessCheck.Evaluate(Array.Empty<TargetMetrics>(), null, null, DateTime.UtcNow);
        string text = StatusSummary.Build(readiness, Array.Empty<TargetMetrics>(), null, DateTime.Now, "0.9.0");
        Assert.Contains("Connection: no active connection", text);
    }
}
