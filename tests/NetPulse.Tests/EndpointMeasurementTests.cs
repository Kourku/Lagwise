using System.Net;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

public class EndpointMeasurementTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TrackedConnection Conn(TransportProtocol p, string? remote, int port = 443, AddressScope? scope = null) => new()
    {
        Id = "x", Protocol = p, Local = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 5000),
        Remote = remote == null ? null : new IPEndPoint(IPAddress.Parse(remote), port),
        Scope = scope ?? (remote == null ? AddressScope.Unspecified : NetworkScopes.Classify(IPAddress.Parse(remote))),
        FirstSeenUtc = T0, LastSeenUtc = T0, Pid = 1
    };

    [Fact]
    public void PublicTcpEndpoint_OffersBothMethods_WithCaveats()
    {
        var o = EndpointMeasurementAdvisor.Advise(Conn(TransportProtocol.Tcp, "34.1.2.3"));
        Assert.All(o, x => Assert.True(x.IsAvailable));
        Assert.Contains("filter or ignore ICMP", o.Single(x => x.Method == EndpointMethod.Icmp).Explanation);
        Assert.Contains("may not be the machine running your match", o.Single(x => x.Method == EndpointMethod.TcpConnect).Explanation);
    }

    [Fact]
    public void UdpSocketWithoutRemote_HasNothingToMeasure()
    {
        var o = EndpointMeasurementAdvisor.Advise(Conn(TransportProtocol.Udp, null));
        Assert.All(o, x => Assert.False(x.IsAvailable));
        Assert.Null(EndpointMeasurementAdvisor.BuildTarget(Conn(TransportProtocol.Udp, null), EndpointMethod.Icmp, out var msg));
        Assert.Contains("remote peer", msg);
    }

    [Fact]
    public void UdpEndpointWithAKnownRemote_AllowsIcmpButNotTcp()
    {
        var o = EndpointMeasurementAdvisor.Advise(Conn(TransportProtocol.Udp, "34.1.2.3", 7777));
        Assert.True(o.Single(x => x.Method == EndpointMethod.Icmp).IsAvailable);
        Assert.False(o.Single(x => x.Method == EndpointMethod.TcpConnect).IsAvailable);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.50")]
    [InlineData("224.0.0.251")]
    public void NonRemoteAddresses_AreNotMeaningfulTargets(string ip)
    {
        var c = Conn(TransportProtocol.Tcp, ip);
        Assert.All(EndpointMeasurementAdvisor.Advise(c), x => Assert.False(x.IsAvailable));
        Assert.Null(EndpointMeasurementAdvisor.BuildTarget(c, EndpointMethod.Icmp, out _));
    }

    [Fact]
    public void BuiltTargets_AreLabelledGameProcessEndpoints_NeverGameServers()
    {
        var icmp = EndpointMeasurementAdvisor.BuildTarget(Conn(TransportProtocol.Tcp, "34.1.2.3"), EndpointMethod.Icmp, out _)!;
        var tcp = EndpointMeasurementAdvisor.BuildTarget(Conn(TransportProtocol.Tcp, "34.1.2.3", 8393), EndpointMethod.TcpConnect, out _)!;

        Assert.Equal(MeasurementKind.GameProcessEndpoint, icmp.Kind);
        Assert.Equal(NetworkProtocol.Icmp, icmp.Protocol);
        Assert.Equal(NetworkProtocol.Tcp, tcp.Protocol);
        Assert.Equal(8393, tcp.Port);
        Assert.StartsWith("game:", icmp.Id);
        Assert.NotEqual(icmp.Id, tcp.Id);
        Assert.Contains("not confirmed as the match server", icmp.Description);
    }

    [Fact]
    public void IPv6Endpoint_BuildsATargetWithoutScopeSuffix()
    {
        var c = Conn(TransportProtocol.Tcp, "2606:4700::1111");
        var t = EndpointMeasurementAdvisor.BuildTarget(c, EndpointMethod.Icmp, out _)!;
        Assert.Equal("2606:4700::1111", t.Host);
    }

    // ---- engine integration: game endpoints use the shared engine but never pollute reference diagnostics ----

    private sealed class Rig
    {
        public EventLogService Events { get; } = new();
        public MeasurementEngine Engine { get; }
        public AppSettings Settings { get; } = new();

        public Rig()
        {
            Settings.Targets = new List<TargetConfig>
            {
                new() { Id = "gw", Name = "Gateway", Host = "192.168.0.1", IsGateway = true },
                new() { Id = "ref", Name = "Reference", Host = "203.0.113.1" }
            };
            Settings.RecentWindowSeconds = 60;
            Settings.EventDedupSeconds = 0;
            Engine = new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(),
                new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), Events) { Settings = Settings };
        }

        public static Measurement M(string id, int s, bool ok, double rtt = 20) => new()
        {
            TargetId = id, TargetName = id, TargetHost = "h", Timestamp = T0.AddSeconds(s), Success = ok, RttMs = ok ? rtt : 0,
            ErrorMessage = ok ? null : "timeout", Protocol = NetworkProtocol.Icmp
        };

        public void Cycle(int s, bool endpointOk, string endpointId, double endpointRtt = 40) =>
            Engine.IngestCycle(new[] { M("gw", s, true, 1), M("ref", s, true, 20), M(endpointId, s, endpointOk, endpointRtt) });

        public TargetMetrics Metrics(string id) => Engine.GetCurrentMetrics().Single(m => m.TargetId == id);
        public IReadOnlyList<ConnectionEvent> Log(string type) => Events.GetRecentEvents(1000).Where(e => e.EventType == type).ToList();
    }

    private static TargetConfig EndpointTarget(string id = "game:Icmp:34.1.2.3:443") => new()
    {
        Id = id, Name = "34.1.2.3 (ICMP)", Host = "34.1.2.3", Kind = MeasurementKind.GameProcessEndpoint
    };

    [Fact]
    public void EndpointThatNeverAnswers_RaisesNoLossOrOutageEvents()
    {
        var rig = new Rig();
        var t = EndpointTarget();
        rig.Engine.AddDynamicTarget(t);
        for (int s = 0; s < 30; s++) rig.Cycle(s, false, t.Id);

        Assert.Equal(MeasurementKind.GameProcessEndpoint, rig.Metrics(t.Id).Kind);
        Assert.Equal(100.0, rig.Metrics(t.Id).PacketLossPercent);
        Assert.Empty(rig.Log("PacketLoss"));
        Assert.Empty(rig.Log("GameEndpointUnreachable"));
    }

    [Fact]
    public void EndpointThatAnsweredThenWentSilent_IsReportedOnceAndRecovers()
    {
        var rig = new Rig();
        var t = EndpointTarget();
        rig.Engine.AddDynamicTarget(t);
        for (int s = 0; s < 5; s++) rig.Cycle(s, true, t.Id);
        for (int s = 5; s < 15; s++) rig.Cycle(s, false, t.Id);

        Assert.Single(rig.Log("GameEndpointUnreachable"));
        Assert.Contains("may be filtering", rig.Log("GameEndpointUnreachable")[0].Description);

        rig.Cycle(15, true, t.Id);
        Assert.Contains(rig.Log("Restored"), e => e.Description.Contains("answering probes again"));
    }

    [Fact]
    public void GameEndpoints_AreExcludedFromReferenceDiagnosis()
    {
        var rig = new Rig();
        var t = EndpointTarget();
        rig.Engine.AddDynamicTarget(t);
        for (int s = 0; s < 40; s++) rig.Cycle(s, false, t.Id); // endpoint dead the whole time

        // the general diagnosis must not be dragged down by a user-selected endpoint
        Assert.Equal(0.0, rig.Engine.CurrentDiagnosis.PacketLossPercent);
        Assert.NotEqual(DiagnosticStatus.Critical, rig.Engine.CurrentDiagnosis.Status);
    }

    [Fact]
    public void RemovingAnEndpoint_StopsItsMetrics_AndLateResultsDoNotResurrectIt()
    {
        var rig = new Rig();
        var t = EndpointTarget();
        rig.Engine.AddDynamicTarget(t);
        rig.Cycle(0, true, t.Id);
        Assert.Contains(rig.Engine.GetCurrentMetrics(), m => m.TargetId == t.Id);

        Assert.True(rig.Engine.RemoveDynamicTarget(t.Id));
        rig.Cycle(1, true, t.Id); // in-flight probe finishing after removal

        Assert.DoesNotContain(rig.Engine.GetCurrentMetrics(), m => m.TargetId == t.Id);
        Assert.False(rig.Engine.RemoveDynamicTarget(t.Id));
    }

    [Fact]
    public void EndpointFailuresDoNotCountAsAnInternetDisconnect()
    {
        var rig = new Rig();
        var t = EndpointTarget();
        rig.Engine.AddDynamicTarget(t);
        // reference + gateway succeed, endpoint fails
        for (int s = 0; s < 10; s++) rig.Cycle(s, false, t.Id);
        Assert.Empty(rig.Log("Disconnected"));

        // only the endpoint measurement present and failing: still not a disconnect
        for (int s = 10; s < 15; s++) rig.Engine.IngestCycle(new[] { Rig.M(t.Id, s, false) });
        Assert.Empty(rig.Log("Disconnected"));
    }
}
