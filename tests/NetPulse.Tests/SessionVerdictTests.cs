using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

/// <summary>"Was it my connection?": the verdict for a recording, built from NetPulse's own measurements only.</summary>
public class SessionVerdictTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 19, 0, 0, DateTimeKind.Utc);

    private static double? Steady(string id, int second) => id switch
    {
        "gw" => 3, "a" => 20, "b" => 22, "c" => 25, "https" => 60, "dns" => 10, _ => null
    };

    /// <summary>A recording with one probe per second to the router, three ping servers, an HTTPS handshake and a DNS lookup.</summary>
    private static MonitoringSession Session(int seconds, Func<string, int, double?>? rtt = null, string iface = "Ethernet",
        Func<int, (double Rx, double? Cpu)>? pc = null)
    {
        rtt ??= Steady;
        var s = new MonitoringSession { StartedUtc = T0, EndedUtc = T0.AddSeconds(seconds), InterfaceType = iface, IsComplete = true, MonitoringIntervalMs = 1000 };
        s.Targets.Add(new SessionTarget { Id = "gw", Name = "Gateway", Protocol = NetworkProtocol.Icmp, Kind = MeasurementKind.LocalGateway });
        foreach (var id in new[] { "a", "b", "c" })
            s.Targets.Add(new SessionTarget { Id = id, Name = "Server " + id, Protocol = NetworkProtocol.Icmp, Kind = MeasurementKind.ReferenceEndpoint });
        s.Targets.Add(new SessionTarget { Id = "https", Name = "cloudflare.com", Protocol = NetworkProtocol.Https, Kind = MeasurementKind.ReferenceEndpoint });
        s.Targets.Add(new SessionTarget { Id = "dns", Name = "DNS", Protocol = NetworkProtocol.Dns, Kind = MeasurementKind.DnsResolution });
        for (int i = 0; i < seconds; i++)
        {
            foreach (var t in s.Targets)
            {
                var v = rtt(t.Id, i);
                s.Samples.Add(new SessionSample { T = T0.AddSeconds(i + 0.5), TargetId = t.Id, Ok = v.HasValue, RttMs = v, Error = v.HasValue ? null : "Timed out" });
            }
            if (pc != null)
            {
                var (rx, cpu) = pc(i);
                s.Throughput.Add(new ThroughputSample { T = T0.AddSeconds(i + 0.2), Rx = rx, Tx = 100_000, Cpu = cpu });
            }
        }
        return s;
    }

    private static bool In(int second, int from, int to) => second >= from && second < to;

    [Fact]
    public void ASteadyRecording_IsGood_OnEveryLayer()
    {
        var v = SessionVerdict.Build(Session(1800, pc: _ => (2_000_000, 30)));
        Assert.Equal(VerdictLevel.Good, v.Level);
        Assert.Equal("Your connection was steady", v.Headline);
        Assert.Contains("No lag spikes", v.Summary);
        Assert.Empty(v.Episodes);
        Assert.Null(v.MainCause);
        Assert.All(v.Layers, l => Assert.Equal(VerdictLevel.Good, l.Level));
        Assert.Equal(3, v.RouterTypicalMs);
        Assert.Equal(22, v.InternetTypicalMs);
        Assert.Empty(v.Advice);
    }

    [Fact]
    public void TheRouterItselfSlowing_PointsAtTheHomeNetwork()
    {
        // on Wi-Fi the router (and so everything behind it) is slow for a minute
        var s = Session(1800, (id, i) => In(i, 600, 660) && id != "dns" ? Steady(id, i) + 90 : Steady(id, i), iface: "Wireless80211");
        var v = SessionVerdict.Build(s);

        Assert.Equal(TroubleCause.HomeNetwork, v.MainCause);
        Assert.Equal("Lag came from your Wi-Fi or router", v.Headline);
        var e = Assert.Single(v.Episodes);
        Assert.InRange(e.Duration.TotalSeconds, 55, 70);
        Assert.Equal(VerdictLevel.Fair, v.Layers.Single(l => l.Name == "Wi-Fi and router").Level);
        Assert.Equal(VerdictLevel.Good, v.Layers.Single(l => l.Name == "Provider and internet").Level);
        Assert.Contains(v.Advice, a => a.Contains("network cable"));
    }

    [Fact]
    public void ServersSlowingWhileTheRouterIsFine_PointsBeyondTheRouter()
    {
        var s = Session(1800, (id, i) => In(i, 900, 960) && id is "a" or "b" or "c" ? Steady(id, i) + 150 : Steady(id, i), pc: _ => (1_000_000, 20));
        var v = SessionVerdict.Build(s);

        Assert.Equal(TroubleCause.Provider, v.MainCause);
        Assert.Equal("Lag came from beyond your router", v.Headline);
        Assert.Contains("your provider or the internet", v.Summary);
        Assert.Equal(VerdictLevel.Good, v.Layers.Single(l => l.Name == "Home network").Level);
        Assert.NotEqual(VerdictLevel.Good, v.Layers.Single(l => l.Name == "Provider and internet").Level);
        Assert.Contains(v.Advice, a => a.Contains("provider"));
    }

    [Fact]
    public void ServersSlowingWhileThisPcDownloadsHeavily_BlamesThePcsOwnTraffic()
    {
        var s = Session(1800, (id, i) => In(i, 300, 400) && id is "a" or "b" or "c" ? Steady(id, i) + 200 : Steady(id, i),
            pc: i => (In(i, 290, 410) ? 80_000_000 : 1_000_000, 25));
        var v = SessionVerdict.Build(s);

        Assert.Equal(TroubleCause.PcTraffic, v.MainCause);
        Assert.Equal("Lag came from this PC's own downloads", v.Headline);
        Assert.Contains("slowed the connection", v.Layers.Single(l => l.Name == "This PC").Text);
        Assert.Contains(v.Advice, a => a.Contains("Pause downloads"));
    }

    [Fact]
    public void AnOutage_MakesThePoorestVerdict()
    {
        var s = Session(1800, (id, i) => In(i, 1000, 1090) ? null : Steady(id, i));
        s.Disconnects.Add(new DisconnectRecord { StartUtc = T0.AddSeconds(1003), EndUtc = T0.AddSeconds(1090) });
        var v = SessionVerdict.Build(s);

        Assert.Equal(VerdictLevel.Poor, v.Level);
        Assert.Equal(TroubleCause.Outage, v.MainCause);
        Assert.Equal("Your connection dropped out", v.Headline);
        Assert.InRange(v.OutageSeconds, 86, 88);
        Assert.Contains(v.Advice, a => a.Contains("router's lights"));
    }

    [Fact]
    public void AServerThatAlwaysDropsPings_IsNotEvidence()
    {
        // public resolvers often rate-limit pings: a server unsteady all the time says nothing about this recording
        var s = Session(1800, (id, i) => id == "c" && i % 3 == 0 ? null : Steady(id, i));
        var v = SessionVerdict.Build(s);
        Assert.Equal(VerdictLevel.Good, v.Level);
        Assert.Empty(v.Episodes);
    }

    [Fact]
    public void OneServerAlone_IsNotEnoughToBlameTheConnection()
    {
        var s = Session(1800, (id, i) => In(i, 500, 560) && id == "a" ? 300 : Steady(id, i));
        Assert.Empty(SessionVerdict.Build(s).Episodes);
    }

    [Fact]
    public void OneShortSpikeInAnHour_IsStillGood()
    {
        var s = Session(3600, (id, i) => In(i, 1800, 1804) && id is "a" or "b" or "c" ? 400 : Steady(id, i));
        var v = SessionVerdict.Build(s);
        Assert.Equal(VerdictLevel.Good, v.Level);
        Assert.Single(v.Episodes);
        Assert.Contains("Just one short spike", v.Summary);
    }

    [Fact]
    public void TheHttpsHandshake_IsNotUsedWhenPingsExist()
    {
        // an HTTPS handshake includes server work: it varies on its own and must not decide the verdict
        var s = Session(1800, (id, i) => id == "https" && In(i, 100, 400) ? 900 : Steady(id, i));
        Assert.Equal(VerdictLevel.Good, SessionVerdict.Build(s).Level);
    }

    [Fact]
    public void ATooShortRecording_IsNotJudged()
    {
        var v = SessionVerdict.Build(Session(20));
        Assert.Equal(VerdictLevel.NotEnoughData, v.Level);
        Assert.Equal("Too short to judge", v.Headline);
    }

    [Fact]
    public void AMonitoringGap_IsNeitherTroubleNorMeasured()
    {
        var s = Session(1800, (id, i) => In(i, 600, 1200) ? null : Steady(id, i));
        s.Gaps.Add(new MonitoringGap { StartUtc = T0.AddSeconds(598), EndUtc = T0.AddSeconds(1202) });
        var v = SessionVerdict.Build(s);
        Assert.Equal(VerdictLevel.Good, v.Level);
        Assert.InRange(v.Measured.TotalSeconds, 1150, 1250);
    }

    // ---- lag marks ----

    [Fact]
    public void ALagMark_DuringProviderTrouble_SaysSo_WithNumbers()
    {
        var s = Session(1800, (id, i) => In(i, 900, 960) && id is "a" or "b" or "c" ? Steady(id, i) + 150 : Steady(id, i));
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(930), Note = "rubber banding" });
        var lag = Assert.Single(SessionVerdict.Build(s).LagMarks);

        Assert.Equal(TroubleCause.Provider, lag.Cause);
        Assert.False(lag.ConnectionLookedFine);
        Assert.Equal("The delay was beyond your router", lag.Headline);
        Assert.Contains("up to 170 ms, usually 20 ms", lag.Detail);
        Assert.Equal("rubber banding", lag.Note);
    }

    [Fact]
    public void ALagMark_WhenEverythingWasNormal_PointsFurtherAway_AndSaysWhatNetPulseCannotSee()
    {
        var s = Session(1800);
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(700) });
        var v = SessionVerdict.Build(s);
        var lag = Assert.Single(v.LagMarks);

        Assert.True(lag.ConnectionLookedFine);
        Assert.Null(lag.Cause);
        Assert.Equal("Your connection looked normal at that moment", lag.Headline);
        Assert.Contains("cannot measure the game's server", lag.Detail);
        Assert.Contains("looked normal at the moment you marked lag", v.Summary);
        Assert.Contains(v.Advice, a => a.Contains("game's own ping"));
    }

    [Fact]
    public void ALagMark_WhileTheProcessorRanFlatOut_SaysThePcWasBusy()
    {
        var s = Session(1800, pc: i => (1_000_000, In(i, 690, 710) ? 99 : 30));
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(705) });
        var lag = Assert.Single(SessionVerdict.Build(s).LagMarks);
        Assert.True(lag.ProcessorBusy);
        Assert.True(lag.ConnectionLookedFine);
        Assert.Contains("99%", lag.Detail);
    }

    [Fact]
    public void ALagMark_WhileTheHomeNetworkStalled_BlamesIt()
    {
        var s = Session(1800, (id, i) => In(i, 400, 430) && id != "dns" ? null : Steady(id, i), iface: "Wireless80211");
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(425) });
        var lag = Assert.Single(SessionVerdict.Build(s).LagMarks);
        Assert.Equal(TroubleCause.HomeNetwork, lag.Cause);
        Assert.Equal("Your Wi-Fi or router was slow", lag.Headline);
    }

    [Fact]
    public void ALagMark_WhileNotMeasuring_SaysSo()
    {
        var s = Session(1800, (id, i) => In(i, 600, 1200) ? null : Steady(id, i));
        s.Gaps.Add(new MonitoringGap { StartUtc = T0.AddSeconds(590), EndUtc = T0.AddSeconds(1210) });
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(900) });
        Assert.Equal("NetPulse was not measuring at that moment", Assert.Single(SessionVerdict.Build(s).LagMarks).Headline);
    }

    [Fact]
    public void ALagMark_InAGameWithoutConnections_IsNotNetworkLag()
    {
        var s = Session(1800);
        s.GameDisplayName = "Solo Quest";
        s.GameProcesses.Add(new GameProcessRecord { Pid = 1, Name = "solo", FirstSeenUtc = T0, LastSeenUtc = T0.AddSeconds(1800) });
        s.LagMarkers.Add(new LagMarker { T = T0.AddSeconds(800) });
        var v = SessionVerdict.Build(s);
        Assert.Equal("Your connection was fine, and the game was not online", Assert.Single(v.LagMarks).Headline);
        Assert.Equal(TimeSpan.FromSeconds(1800), v.GameTime);
    }

    // ---- where the verdict shows up ----

    [Fact]
    public void TheReport_StartsWithTheVerdict()
    {
        var s = Session(1800, (id, i) => In(i, 900, 960) && id is "a" or "b" or "c" ? Steady(id, i) + 150 : Steady(id, i));
        s.Name = "Test";
        var md = SessionReportBuilder.BuildMarkdown(s, SessionAnalysis.Compute(s));
        Assert.Contains("## Verdict: Lag came from beyond your router", md);
        Assert.Contains("| Provider and internet |", md);
        Assert.Contains("### When it happened", md);
        Assert.DoesNotContain("Findings", md);
        Assert.True(md.IndexOf("## Verdict", StringComparison.Ordinal) < md.IndexOf("## What was measured", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHistoryIndex_KeepsEachRecordingsVerdict()
    {
        var summary = SessionSummary.From(Session(1800), 1000);
        Assert.Equal(VerdictLevel.Good, summary.Verdict);
        Assert.Equal("Your connection was steady", summary.VerdictHeadline);
    }

    [Fact]
    public void AnInProgressRecording_CanBeJudgedSoFar()
    {
        var s = Session(600);
        s.EndedUtc = null;
        s.IsComplete = false;
        var v = SessionVerdict.Build(s);
        Assert.Equal(VerdictLevel.Good, v.Level);
        Assert.InRange(v.Measured.TotalSeconds, 590, 605);
    }

    [Fact]
    public void AnEmptyRecording_DoesNotFail()
    {
        var v = SessionVerdict.Build(new MonitoringSession { StartedUtc = T0 });
        Assert.Equal(VerdictLevel.NotEnoughData, v.Level);
        Assert.All(v.Layers, l => Assert.Equal(VerdictLevel.NotEnoughData, l.Level));
    }
}
