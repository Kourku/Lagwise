using Lagwise.Core.Games;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class SessionAnalysisTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MonitoringSession Build(Action<MonitoringSession>? tweak = null)
    {
        var s = new MonitoringSession
        {
            Name = "test", GameId = "lol", GameDisplayName = "League of Legends",
            StartedUtc = T0, EndedUtc = T0.AddMinutes(10), IsComplete = true, ActiveInterface = "Ethernet", InterfaceType = "Ethernet",
            Targets =
            {
                new SessionTarget { Id = "gw", Name = "Router", Host = "192.168.0.1", Kind = MeasurementKind.LocalGateway, Protocol = NetworkProtocol.Icmp },
                new SessionTarget { Id = "ref", Name = "Reference", Host = "1.1.1.1", Kind = MeasurementKind.ReferenceEndpoint, Protocol = NetworkProtocol.Icmp }
            }
        };
        // 10 minutes at 1 probe/s: router steady at 1 ms; reference steady at 25 ms except 20 s around minute 5
        for (int sec = 0; sec < 600; sec++)
        {
            s.Samples.Add(new SessionSample { T = T0.AddSeconds(sec), TargetId = "gw", Ok = true, RttMs = 1 });
            bool bad = sec >= 295 && sec < 315;
            s.Samples.Add(new SessionSample
            {
                T = T0.AddSeconds(sec), TargetId = "ref", Ok = !(bad && sec % 2 == 0),
                RttMs = bad ? (sec % 2 == 0 ? null : 180) : 25, Error = bad && sec % 2 == 0 ? "timeout" : null
            });
        }
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void Stats_ComputeLatencyLossAndVariationPerTarget()
    {
        var stats = SessionAnalysis.Compute(Build());

        Assert.Equal(TimeSpan.FromMinutes(10), stats.Duration);
        Assert.Equal(1200, stats.ProbeCount);

        var gw = stats.Targets.Single(t => t.TargetId == "gw");
        Assert.Equal(0, gw.LossPercent);
        Assert.Equal(1, gw.MedianMs);
        Assert.Equal(0, gw.VariationMs);
        Assert.Equal("Your router", gw.KindLabel);

        var r = stats.Targets.Single(t => t.TargetId == "ref");
        Assert.Equal(600, r.Probes);
        Assert.Equal(590, r.Replies);
        Assert.Equal(10 / 600.0 * 100, r.LossPercent, 3);
        Assert.Equal(25, r.MedianMs);
        Assert.Equal(180, r.MaxMs);
        Assert.Equal(1, r.LongestFailureStreak);
        Assert.Contains("not a game server", r.KindLabel);
        Assert.Equal("ICMP ping", r.Method);
    }

    [Fact]
    public void LagMarker_IsCorrelatedWithWhatTheMeasurementsShowedAtThatMoment()
    {
        var s = Build(x =>
        {
            x.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(305), Note = "rubber band" });
            x.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(100), Note = "felt fine" });
            x.Events.Add(new ConnectionEvent { EventType = "LatencySpike", Timestamp = T0.AddSeconds(300), Description = "spike", TargetName = "Reference" });
            x.Events.Add(new ConnectionEvent { EventType = "LatencySpike", Timestamp = T0.AddSeconds(500), Description = "far away", TargetName = "Reference" });
        });
        var stats = SessionAnalysis.Compute(s);

        var near = stats.LagCorrelations[1]; // ordered by time: 100 s first, 305 s second
        Assert.Equal("rubber band", near.Note);
        Assert.Single(near.NearbyEvents);
        var affected = Assert.Single(near.Affected);
        Assert.Equal("Reference", affected.Name);
        Assert.True(affected.WindowFailures >= 5);
        Assert.DoesNotContain(near.Affected, a => a.Name == "Router");

        var calm = stats.LagCorrelations[0];
        Assert.Empty(calm.Affected);
        Assert.Empty(calm.NearbyEvents);
    }

    [Fact]
    public void GapAndOutageDurations_AreSummed_AndOpenOutagesRunToTheEnd()
    {
        var s = Build(x =>
        {
            x.Gaps.Add(new MonitoringGap { StartUtc = T0.AddSeconds(10), EndUtc = T0.AddSeconds(70) });
            x.Disconnects.Add(new DisconnectRecord { StartUtc = T0.AddSeconds(500), EndUtc = null });
        });
        var stats = SessionAnalysis.Compute(s);
        Assert.Equal(60, stats.GapSeconds);
        Assert.Equal(100, stats.DisconnectSeconds);
    }

    [Fact]
    public void Report_ContainsEverythingTheBriefAsksFor_AndNeverClaimsGameServerPing()
    {
        var s = Build(x =>
        {
            x.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(305), Note = "stutter" });
            x.Gaps.Add(new MonitoringGap { StartUtc = T0.AddSeconds(10), EndUtc = T0.AddSeconds(70), Reason = "x" });
            x.Events.Add(new ConnectionEvent { EventType = "LatencySpike", Timestamp = T0.AddSeconds(300), Description = "spike a|b", TargetName = "Reference" });
            x.Connections.Add(new ConnectionRecord { Id = "c", Protocol = "TCP", Remote = "34.1.2.3:443", Scope = "Public", FirstSeenUtc = T0, LastSeenUtc = T0.AddMinutes(5) });
            x.Connections.Add(new ConnectionRecord { Id = "u", Protocol = "UDP", Local = "0.0.0.0:7777", Scope = "Unspecified", FirstSeenUtc = T0, LastSeenUtc = T0.AddMinutes(5) });
            x.GameProcesses.Add(new GameProcessRecord { Pid = 1, Name = "League of Legends", FirstSeenUtc = T0, LastSeenUtc = T0.AddMinutes(9) });
        });
        var md = SessionReportBuilder.BuildMarkdown(s, SessionAnalysis.Compute(s));

        foreach (var expected in new[]
        {
            "League of Legends", "## Verdict:", "| Home network |", "## What was measured", "ICMP ping", "## Latency, variation and probe loss", "| Router |",
            "## Gaps and outages", "not a network outage", "### Moments you marked as laggy", "stutter", "## Events", "Latency spike", "spike a/b",
            "## The game", "ran for 9 min", "1 UDP socket", "1 connection to internet services", "## Limitations",
            "cannot measure a game's own server", "nothing was uploaded"
        })
        {
            Assert.Contains(expected, md);
        }
        Assert.Contains("**not** the ping shown inside a game", md);
        Assert.DoesNotContain("game server ping:", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_ForAnInterruptedSession_SaysSo()
    {
        var s = Build(x => { x.IsComplete = false; x.EndedUtc = null; });
        var md = SessionReportBuilder.BuildMarkdown(s, SessionAnalysis.Compute(s));
        Assert.Contains("recording was interrupted", md);
        Assert.Contains("incomplete", md);
    }

    [Fact]
    public void Report_ForAPlainNetworkSession_OmitsTheGameSection()
    {
        var s = Build(x => { x.GameDisplayName = null; x.GameId = null; });
        var md = SessionReportBuilder.BuildMarkdown(s, SessionAnalysis.Compute(s));
        Assert.DoesNotContain("## Game processes and connections", md);
    }

    [Fact]
    public void SamplesCsv_HasOneRowPerProbe_WithEscapedText()
    {
        var s = Build(x => x.Targets[0].Name = "Router \"main\"");
        var lines = SessionReportBuilder.BuildSamplesCsv(s).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 1200, lines.Length);
        Assert.Equal("TimestampUtc,TimestampLocal,Target,Method,Kind,Success,RttMs,Error", lines[0].Trim());
        Assert.Contains("\"Router \"\"main\"\"\"", lines[1]);
        Assert.Contains("timeout", string.Join("", lines));
    }

    // ---- chart data ----

    [Fact]
    public void Chart_DownsamplesToBuckets_KeepsSpikes_AndMarksLagGapsAndOutages()
    {
        var s = Build(x =>
        {
            x.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(305) });
            x.Gaps.Add(new MonitoringGap { StartUtc = T0.AddSeconds(10), EndUtc = T0.AddSeconds(70) });
            x.Disconnects.Add(new DisconnectRecord { StartUtc = T0.AddSeconds(500), EndUtc = T0.AddSeconds(520) });
        });
        var data = SessionChartBuilder.Build(s, ChartMode.Latency, maxBuckets: 60);

        Assert.Equal(600, data.DurationSeconds);
        Assert.Equal(10, data.BucketSeconds);
        Assert.InRange(data.Series[0].Points.Count, 1, 60);
        Assert.Equal(180, data.Series.Single(x => x.TargetId == "ref").Points.Max(p => p.Y)); // the slowest reply survives downsampling
        Assert.Equal(305, data.LagMarkerSeconds.Single());
        Assert.Equal((10.0, 70.0), data.GapSeconds.Single());
        Assert.Equal((500.0, 520.0), data.OutageSeconds.Single());
        Assert.Equal("ms", data.Unit);
    }

    [Fact]
    public void Chart_LossAndVariationModes_UseTheirOwnUnitsAndFilterTargets()
    {
        var s = Build();
        var loss = SessionChartBuilder.Build(s, ChartMode.Loss, new HashSet<string> { "ref" });
        Assert.Equal("%", loss.Unit);
        Assert.Equal(100, loss.YCap);
        Assert.Single(loss.Series);
        Assert.Contains(loss.Series[0].Points, p => p.Y > 0);

        var variation = SessionChartBuilder.Build(s, ChartMode.Variation);
        Assert.Equal(0, variation.Series.Single(x => x.TargetId == "gw").Points.Max(p => p.Y));
        Assert.True(variation.Series.Single(x => x.TargetId == "ref").Points.Max(p => p.Y) > 50);
    }

    [Fact]
    public void Chart_ClipsOneHugeSpike_SoTheRestStaysReadable()
    {
        var s = Build(x => x.Samples.Add(new SessionSample { T = T0.AddSeconds(100), TargetId = "ref", Ok = true, RttMs = 5000 }));
        var data = SessionChartBuilder.Build(s, ChartMode.Latency, maxBuckets: 600);
        Assert.True(data.YCap < 1000, $"cap was {data.YCap}");
    }

    [Fact]
    public void Chart_OfAnEmptySession_DoesNotThrow()
    {
        var s = new MonitoringSession { StartedUtc = T0, EndedUtc = T0 };
        var data = SessionChartBuilder.Build(s, ChartMode.Latency);
        Assert.Empty(data.Series);
        Assert.True(data.YCap > 0);
        Assert.Equal(0, SessionAnalysis.Compute(s).ProbeCount);
    }

    // ---- engine target sync after the Settings screen removes a target ----

    [Fact]
    public void Engine_SyncTargets_DropsStatisticsOfRemovedTargets()
    {
        var settings = new AppSettings();
        settings.Targets = new List<TargetConfig>
        {
            new() { Id = "a", Name = "A", Host = "203.0.113.1" },
            new() { Id = "b", Name = "B", Host = "203.0.113.2" }
        };
        var engine = new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(), new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), new EventLogService()) { Settings = settings };
        Measurement M(string id) => new() { TargetId = id, TargetName = id, TargetHost = "h", Timestamp = T0, Success = true, RttMs = 20 };
        engine.IngestCycle(new[] { M("a"), M("b") });
        Assert.Equal(2, engine.GetCurrentMetrics().Count);

        settings.Targets = settings.Targets.Where(t => t.Id == "a").ToList();
        engine.SyncTargets();

        Assert.Equal("a", Assert.Single(engine.GetCurrentMetrics()).TargetId);
    }
}
