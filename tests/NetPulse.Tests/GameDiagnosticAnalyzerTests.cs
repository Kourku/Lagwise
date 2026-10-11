using System.Net;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

public class GameDiagnosticAnalyzerTests
{
    private static TargetMetrics Healthy(string id, MeasurementKind kind, double baseline = 20, string? host = null) => new()
    {
        TargetId = id, TargetName = id, TargetHost = host ?? id, Kind = kind,
        CurrentLatency = baseline, BaselineLatencyMs = baseline, MedianLatency = baseline,
        TotalProbes = 60, FailedProbes = 0, RecentSampleCount = 60, RecentWindowSeconds = 60, PacketLossPercent = 0, Jitter = 1, IsReachable = true,
        IsGateway = kind == MeasurementKind.LocalGateway,
        Protocol = NetworkProtocol.Icmp
    };

    private static TargetMetrics WithLoss(TargetMetrics m, double loss) { m.PacketLossPercent = loss; return m; }
    private static TargetMetrics Elevated(TargetMetrics m) { m.CurrentLatency = m.BaselineLatencyMs * 4; return m; }

    private static IReadOnlyList<GameFinding> Run(IEnumerable<TargetMetrics> metrics, GameProcessState? process = null,
        IReadOnlyList<TrackedConnection>? connections = null, IReadOnlyList<ConnectionChange>? changes = null) =>
        GameDiagnosticAnalyzer.Analyze(new GameDiagnosticInput
        {
            Metrics = metrics.ToList(), Process = process,
            ActiveConnections = connections ?? Array.Empty<TrackedConnection>(),
            RecentChanges = changes ?? Array.Empty<ConnectionChange>()
        });

    private static TargetMetrics Gw() => Healthy("gw", MeasurementKind.LocalGateway, 1);
    private static TargetMetrics Ref(string id) => Healthy(id, MeasurementKind.ReferenceEndpoint, 30);

    private static GameFinding? Find(IReadOnlyList<GameFinding> f, string idPrefix) => f.FirstOrDefault(x => x.Id.StartsWith(idPrefix));

    [Fact]
    public void AllHealthy_ReportsNoProblem_WithoutBlamingAnyone()
    {
        var f = Run(new[] { Gw(), Ref("a"), Ref("b"), Ref("c") });

        var ok = Assert.Single(f);
        Assert.Equal("no-problem-observed", ok.Id);
        Assert.Equal(FindingSeverity.Info, ok.Severity);
        Assert.Contains("not measured", string.Join(" ", ok.PossibleExplanations), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GatewayAndSeveralReferencesDegrade_PointsToLocalNetwork_AsPossible()
    {
        var f = Run(new[] { WithLoss(Gw(), 12), WithLoss(Ref("a"), 10), Elevated(Ref("b")), Ref("c") });

        var local = Find(f, "local-network")!;
        Assert.Contains("Possible", local.Title);
        Assert.Equal(FindingConfidence.Low, local.Confidence); // c stayed normal -> contradiction lowers a Medium finding
        Assert.NotEmpty(local.Contradictions);
        Assert.NotEmpty(local.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(local.NextTest));
        Assert.Null(Find(f, "beyond-local"));
    }

    [Fact]
    public void LocalProblemWithNoContradiction_GetsHigherConfidence()
    {
        var f = Run(new[] { WithLoss(Gw(), 12), WithLoss(Ref("a"), 10), WithLoss(Ref("b"), 9), WithLoss(Ref("c"), 8) });

        Assert.Equal(FindingConfidence.High, Find(f, "local-network")!.Confidence);
    }

    [Fact]
    public void StableGatewayAndSeveralIndependentReferencesDegrade_PointsBeyondLocal_NotBlamingTheIsp()
    {
        var f = Run(new[] { Gw(), WithLoss(Ref("a"), 9), WithLoss(Ref("b"), 11), WithLoss(Ref("c"), 7) });

        var beyond = Find(f, "beyond-local")!;
        Assert.Equal(FindingConfidence.High, beyond.Confidence);
        Assert.Contains("Possible", beyond.Title);
        var text = string.Join(" ", beyond.PossibleExplanations);
        Assert.Contains("ISP", text);
        Assert.Contains("reference providers", text); // alternatives are listed, not one blamed cause
        Assert.DoesNotContain("Your ISP is", text);
        Assert.Contains(beyond.Evidence, e => e.Contains("Router: stable"));
    }

    [Fact]
    public void OneDegradedReference_IsNotEnoughToBlameTheConnection()
    {
        var f = Run(new[] { Gw(), WithLoss(Ref("a"), 30), Ref("b"), Ref("c") });

        Assert.Null(Find(f, "beyond-local"));
        Assert.Null(Find(f, "local-network"));
        Assert.Equal(FindingConfidence.Low, Find(f, "single-reference")!.Confidence);
    }

    [Fact]
    public void GatewayOnlyDegradation_IsLowConfidence_AndExplainsIcmpDeprioritisation()
    {
        var f = Run(new[] { WithLoss(Gw(), 20), Ref("a"), Ref("b"), Ref("c") });

        var g = Find(f, "gateway-only")!;
        Assert.Equal(FindingConfidence.Low, g.Confidence);
        Assert.Contains("deprioritise", string.Join(" ", g.PossibleExplanations));
        Assert.Equal(3, g.Contradictions.Count);
    }

    [Fact]
    public void ElevatedLatency_IsJudgedAgainstTheTargetsOwnBaseline_NotAFixedNumber()
    {
        // 150 ms is this user's normal for a distant target: not degraded
        var far = Healthy("far", MeasurementKind.ReferenceEndpoint, 150);
        Assert.False(GameDiagnosticAnalyzer.IsDegraded(far, out _));

        far.CurrentLatency = 600;
        Assert.True(GameDiagnosticAnalyzer.IsDegraded(far, out var reason));
        Assert.Contains("typical", reason);
    }

    [Fact]
    public void TooFewSamples_NeverProducesAConclusion()
    {
        var gw = WithLoss(Gw(), 50); gw.RecentSampleCount = 3;
        var a = WithLoss(Ref("a"), 50); a.RecentSampleCount = 3;
        var f = Run(new[] { gw, a });

        Assert.Equal("insufficient-data", Assert.Single(f).Id);
        Assert.Equal(FindingConfidence.Low, f[0].Confidence);
    }

    // ---- regression: numbers taken from a real 36-minute League of Legends session ----

    [Fact]
    public void ChronicallyNoisyReferences_AreNotCountedAsEvidenceOfANewProblem()
    {
        // router and Google rock-steady; Quad9 lost ~5% of ICMP all session; Cloudflare HTTPS has a long latency tail
        var quad9 = WithLoss(Ref("quad9"), 5); quad9.TotalProbes = 2090; quad9.FailedProbes = 113; quad9.SessionPacketLossPercent = 5.4;
        var https = Elevated(Ref("https")); https.RecentHistory = Enumerable.Range(0, 120).Select(i => new Measurement { Success = true, RttMs = 96 }).ToList();
        https.MedianLatency = 97; https.P95Latency = 427; https.CurrentLatency = 450;
        var cf = WithLoss(Ref("cf"), 5);

        var f = Run(new[] { Gw(), Ref("google"), cf, quad9, https });

        Assert.Null(Find(f, "beyond-local"));
        Assert.Null(Find(f, "local-network"));
        var noisy = Find(f, "noisy-references")!;
        Assert.Equal(FindingSeverity.Info, noisy.Severity);
        Assert.Equal(2, noisy.Observations.Count);
        Assert.Contains("rate-limit", string.Join(" ", noisy.PossibleExplanations));
    }

    [Fact]
    public void ANewProblemOnSteadyReferences_StillRaisesTheFinding()
    {
        var f = Run(new[] { Gw(), WithLoss(Ref("a"), 9), WithLoss(Ref("b"), 11), WithLoss(Ref("c"), 7) });
        Assert.NotNull(Find(f, "beyond-local"));
        Assert.Null(Find(f, "noisy-references"));
    }

    // ---- game-process endpoints ----

    private static TargetMetrics Endpoint(string host = "34.1.2.3") => Healthy("game:Icmp:" + host + ":443", MeasurementKind.GameProcessEndpoint, 40, host);

    private static TrackedConnection Conn(string remote, bool active = true)
    {
        var c = new TrackedConnection
        {
            Id = "c|" + remote, Protocol = TransportProtocol.Tcp, Local = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 5000),
            Remote = new IPEndPoint(IPAddress.Parse(remote), 443), Scope = AddressScope.Public, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, Pid = 1
        };
        if (!active) c.EndedUtc = DateTime.UtcNow;
        return c;
    }

    [Fact]
    public void EndpointThatNeverAnswersIcmp_IsNotCalledOffline_AndMentionsFiltering()
    {
        var e = Endpoint();
        e.TotalProbes = 30; e.FailedProbes = 30; e.PacketLossPercent = 100; e.IsReachable = false;
        var f = Run(new[] { Gw(), Ref("a"), Ref("b"), e }, connections: new[] { Conn("34.1.2.3") });

        var silent = Find(f, "endpoint-silent")!;
        Assert.Equal(FindingConfidence.Low, silent.Confidence);
        Assert.Contains("filter", string.Join(" ", silent.PossibleExplanations));
        Assert.DoesNotContain("offline", silent.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still has an active connection", string.Join(" ", silent.Contradictions));
        Assert.Contains("TCP", silent.NextTest);
    }

    [Fact]
    public void EndpointDegradesWhileReferencesStayStable_RaisesPossibleGameSpecificIssue_Unproven()
    {
        var e = Endpoint(); e.CurrentLatency = 400;
        var f = Run(new[] { Gw(), Ref("a"), Ref("b"), Ref("c"), e }, connections: new[] { Conn("34.1.2.3", active: false) });

        var s = Find(f, "endpoint-specific")!;
        Assert.Equal(FindingConfidence.Medium, s.Confidence);
        Assert.Contains("not proven", string.Join(" ", s.PossibleExplanations));
        Assert.Contains("role is unknown", string.Join(" ", s.PossibleExplanations));
    }

    [Fact]
    public void EndpointProblem_WithStillActiveConnection_LowersConfidence()
    {
        var e = Endpoint(); e.CurrentLatency = 400;
        var f = Run(new[] { Gw(), Ref("a"), Ref("b"), Ref("c"), e }, connections: new[] { Conn("34.1.2.3") });

        var s = Find(f, "endpoint-specific")!;
        Assert.Equal(FindingConfidence.Low, s.Confidence);
        Assert.NotEmpty(s.Contradictions);
    }

    [Fact]
    public void EndpointDegradingWithReferences_IsReportedAsConsistentWithBroaderProblem()
    {
        var e = Endpoint(); e.CurrentLatency = 400;
        var f = Run(new[] { Gw(), WithLoss(Ref("a"), 9), WithLoss(Ref("b"), 9), Ref("c"), e });

        Assert.NotNull(Find(f, "endpoint-with-references"));
        Assert.Null(Find(f, "endpoint-specific"));
    }

    [Fact]
    public void ContradictoryMeasurements_ReferencesBadButEndpointSteady_LowersBeyondLocalConfidence()
    {
        var e = Endpoint();
        var f = Run(new[] { Gw(), WithLoss(Ref("a"), 9), WithLoss(Ref("b"), 9), WithLoss(Ref("c"), 9), e });

        Assert.Contains("stayed normal", string.Join(" ", Find(f, "beyond-local")!.Contradictions));
        Assert.Equal(FindingConfidence.Medium, Find(f, "beyond-local")!.Confidence);
        var contra = Find(f, "contradiction")!;
        Assert.Equal(FindingConfidence.Low, contra.Confidence);
        Assert.Contains("Not enough information to blame", string.Join(" ", contra.PossibleExplanations));
    }

    [Fact]
    public void RecentlyEndedConnections_AreAnObservation_NotACause()
    {
        var gone = Conn("34.9.9.9", active: false);
        var change = new ConnectionChange(ConnectionChangeKind.Disappeared, gone, null, DateTime.UtcNow, "TCP connection to 34.9.9.9:443 is no longer observed.");
        var f = Run(new[] { Gw(), Ref("a"), Ref("b") }, changes: new[] { change });

        var c = Find(f, "connection-changes")!;
        Assert.Equal(FindingSeverity.Info, c.Severity);
        Assert.Contains("Normal", string.Join(" ", c.PossibleExplanations));
    }

    [Fact]
    public void GameNotRunning_IsStatedClearly()
    {
        var state = new GameProcessState { Profile = GameProfiles.Find("lol")!, State = GameRunState.LauncherOnly, Message = "launcher only" };
        var f = Run(new[] { Gw(), Ref("a"), Ref("b") }, state);

        Assert.Equal(FindingConfidence.High, Find(f, "game-not-running")!.Confidence);
    }

    [Fact]
    public void EveryFinding_HasObservationsAndANextTest()
    {
        var e = Endpoint(); e.CurrentLatency = 400;
        var scenarios = new[]
        {
            new[] { WithLoss(Gw(), 12), WithLoss(Ref("a"), 10), WithLoss(Ref("b"), 9), Ref("c") },
            new[] { Gw(), WithLoss(Ref("a"), 9), WithLoss(Ref("b"), 9), Ref("c") },
            new[] { WithLoss(Gw(), 20), Ref("a"), Ref("b") },
            new[] { Gw(), Ref("a"), Ref("b"), Ref("c"), e },
        };

        foreach (var s in scenarios)
            foreach (var f in Run(s))
            {
                Assert.NotEmpty(f.Observations);
                Assert.False(string.IsNullOrWhiteSpace(f.NextTest), f.Id);
                Assert.False(string.IsNullOrWhiteSpace(f.Title));
            }
    }
}
