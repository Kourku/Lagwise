using Lagwise.Core.Games;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class ReadinessAndComparisonTests
{
    private static TargetMetrics Gw(double median = 1.2, double jitter = 0.3, double loss = 0) => new()
    {
        TargetId = "gateway", TargetName = "Router", IsGateway = true, Protocol = NetworkProtocol.Icmp,
        MedianLatency = median, CurrentLatency = median, Jitter = jitter, PacketLossPercent = loss, RecentSampleCount = 60, TotalProbes = 60
    };

    private static TargetMetrics Ref(string id, double latency = 25, double jitter = 1, double loss = 0, double baseline = 25) => new()
    {
        TargetId = id, TargetName = id, Protocol = NetworkProtocol.Icmp, MedianLatency = latency, CurrentLatency = latency, Jitter = jitter,
        PacketLossPercent = loss, RecentSampleCount = 60, TotalProbes = 60, BaselineLatencyMs = baseline
    };

    private static ReadinessResult Eval(params TargetMetrics[] m) => ReadinessCheck.Evaluate(m, new NetworkInterfaceInfo(), null, DateTime.UtcNow);

    [Fact]
    public void HealthyConnection_IsGoodToPlay_EvenWithoutABufferbloatTest()
    {
        var r = Eval(Gw(), Ref("a"), Ref("b"), Ref("c"));
        Assert.Equal(CheckStatus.Good, r.Overall);
        Assert.Equal(CheckStatus.Unknown, r.Items.Single(i => i.Id == "load").Status);
    }

    [Fact]
    public void LossAtTheRouter_ThatReachesTheTraffic_IsPoor_AndPointsAtTheHomeNetwork()
    {
        var r = Eval(Gw(loss: 5), Ref("a", loss: 4), Ref("b", loss: 3));
        Assert.Equal(CheckStatus.Poor, r.Items.Single(i => i.Id == "router").Status);
        Assert.Equal(CheckStatus.Poor, r.Overall);
        Assert.Contains("Wi-Fi, a cable or the router", r.Items.Single(i => i.Id == "router").Detail);
    }

    [Fact]
    public void RouterSkippingItsOwnPings_WhileTrafficThroughItIsComplete_IsNoProblem()
    {
        // routers answer pings last: missing replies with every internet server answering fully are not lost traffic
        var r = Eval(Gw(loss: 5), Ref("a"), Ref("b"), Ref("c"));
        var router = r.Items.Single(i => i.Id == "router");
        Assert.Equal(CheckStatus.Good, router.Status);
        Assert.Contains("reaches the internet steadily", router.Detail);
        Assert.Equal(CheckStatus.Good, r.Overall);
    }

    [Fact]
    public void RouterSwingingOverCable_IsOnlyBlamedWhenTheInternetServersSwingToo()
    {
        // the case seen on a healthy wired PC: router 3.7 ms swinging 3.8 ms, internet steady at about 4.5 ms
        Assert.Equal(CheckStatus.Good, Eval(Gw(3.7, 3.8), Ref("a", jitter: 4.5), Ref("b", jitter: 4.7)).Items.Single(i => i.Id == "router").Status);
        // a router swinging 12 ms over cable while the servers behind it swing as much: the home network is the place to look
        Assert.Equal(CheckStatus.Fair, Eval(Gw(4, 12), Ref("a", jitter: 12), Ref("b", jitter: 11)).Items.Single(i => i.Id == "router").Status);
    }

    [Fact]
    public void SlowRouterReplies_AreExcused_WhenAnInternetServerAnswersFasterThanTheRouter()
    {
        // 20 ms from the router but 6 ms from a server beyond it: the router is just slow to answer its own pings
        Assert.Equal(CheckStatus.Good, Eval(Gw(20, 2), Ref("a", latency: 6), Ref("b", latency: 30)).Items.Single(i => i.Id == "router").Status);
        // 20 ms from the router and everything beyond it slower still: the first step is slow
        Assert.Equal(CheckStatus.Fair, Eval(Gw(20, 2), Ref("a", latency: 35), Ref("b", latency: 40)).Items.Single(i => i.Id == "router").Status);
    }

    [Fact]
    public void Vpn_IsNamed_WithoutTurningTheVerdictAmber()
    {
        var nic = new NetworkInterfaceInfo { TunnelName = "NordLynx (NordLynx Tunnel)" };
        var r = ReadinessCheck.Evaluate(new[] { Gw(), Ref("a"), Ref("b") }, nic, null, DateTime.UtcNow);
        Assert.Contains("NordLynx", r.Items.Single(i => i.Id == "vpn").Detail);
        Assert.Equal(CheckStatus.Good, r.Overall);
        Assert.DoesNotContain(ReadinessCheck.Evaluate(new[] { Gw(), Ref("a"), Ref("b") }, new NetworkInterfaceInfo(), null, DateTime.UtcNow).Items, i => i.Id == "vpn");
    }

    [Fact]
    public void OneLossyServer_IsNotBlamedOnTheConnection_ButSeveralAre()
    {
        Assert.Equal(CheckStatus.Good, Eval(Gw(), Ref("a", loss: 10), Ref("b"), Ref("c")).Items.Single(i => i.Id == "loss").Status);
        Assert.Equal(CheckStatus.Poor, Eval(Gw(), Ref("a", loss: 10), Ref("b", loss: 8), Ref("c")).Items.Single(i => i.Id == "loss").Status);
    }

    [Fact]
    public void UnsteadyPing_IsFairThenPoor()
    {
        Assert.Equal(CheckStatus.Fair, Eval(Gw(), Ref("a", jitter: 12), Ref("b", jitter: 12)).Items.Single(i => i.Id == "stability").Status);
        Assert.Equal(CheckStatus.Poor, Eval(Gw(), Ref("a", jitter: 30), Ref("b", jitter: 30)).Items.Single(i => i.Id == "stability").Status);
    }

    [Fact]
    public void NotEnoughData_IsUnknown_NotGood()
    {
        var r = ReadinessCheck.Evaluate(new[] { new TargetMetrics { TargetId = "gateway", IsGateway = true, RecentSampleCount = 2 } }, null, null, DateTime.UtcNow);
        Assert.Equal(CheckStatus.Unknown, r.Overall);
    }

    [Fact]
    public void BufferbloatGrade_IsUsed_WithItsAge()
    {
        var now = DateTime.UtcNow;
        var bb = new BufferbloatResult { Grade = "D", DownloadDeltaMs = 120, Timestamp = now.AddHours(-3) };
        var r = ReadinessCheck.Evaluate(new[] { Gw(), Ref("a"), Ref("b") }, null, bb, now);
        var load = r.Items.Single(i => i.Id == "load");
        Assert.Equal(CheckStatus.Poor, load.Status);
        Assert.Contains("3 h ago", load.Detail);
        Assert.Equal(CheckStatus.Poor, r.Overall);
    }

    [Fact]
    public void WeakWifi_IsReported_AndCableIsNeverCheckedForSignal()
    {
        var wifi = new NetworkInterfaceInfo { IsWifi = true, WifiSignalPercent = 30 };
        Assert.Equal(CheckStatus.Poor, ReadinessCheck.Evaluate(new[] { Gw(), Ref("a"), Ref("b") }, wifi, null, DateTime.UtcNow).Items.Single(i => i.Id == "wifi").Status);
        Assert.DoesNotContain(Eval(Gw(), Ref("a")).Items, i => i.Id == "wifi");
    }

    private static TargetStats Stats(string id, double median, double variation, double loss) =>
        new(id, id, MeasurementKind.ReferenceEndpoint, "ICMP", 100, 100, loss, median, median, median + 5, median + 10, variation, 0);

    [Fact]
    public void Comparison_MatchesTargetsById_AndIgnoresNoise()
    {
        var a = new SessionStats { Targets = new[] { Stats("cf", 30, 6, 1), Stats("g", 25, 5, 0), Stats("old", 10, 1, 0) } };
        var b = new SessionStats { Targets = new[] { Stats("cf", 20, 2, 0), Stats("g", 26, 5, 0), Stats("new", 10, 1, 0) } };

        var r = SessionComparison.Compare(a, b);

        Assert.Equal(new[] { "cf", "g" }, r.Targets.Select(t => t.TargetId));
        Assert.Equal(-10, r.Targets[0].MedianDelta);
        Assert.Contains("old", r.OnlyInA);
        Assert.Contains("new", r.OnlyInB);
        Assert.Contains("lower by", r.Summary);      // median of (-10, +1) = -4.5 ms: beyond the 3 ms noise
        Assert.Contains("steadier", r.Summary);
    }

    [Fact]
    public void Comparison_WithNothingInCommon_SaysSo()
    {
        var r = SessionComparison.Compare(new SessionStats { Targets = new[] { Stats("a", 1, 1, 0) } }, new SessionStats { Targets = new[] { Stats("b", 1, 1, 0) } });
        Assert.Empty(r.Targets);
        Assert.Contains("cannot be compared", r.Summary);
    }
}
