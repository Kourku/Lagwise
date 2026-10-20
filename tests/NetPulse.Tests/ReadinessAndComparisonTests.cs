using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

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
    public void LossAtTheRouter_IsPoor_AndPointsAtTheHomeNetwork()
    {
        var r = Eval(Gw(loss: 5), Ref("a"), Ref("b"));
        Assert.Equal(CheckStatus.Poor, r.Overall);
        Assert.Contains("Wi-Fi, a cable or the router", r.Items.Single(i => i.Id == "router").Detail);
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
