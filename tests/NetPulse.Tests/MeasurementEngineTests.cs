using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Tests;

public class MeasurementEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public EventLogService Events { get; } = new();
        public MeasurementEngine Engine { get; }
        public AppSettings Settings { get; } = new();

        public Rig(Action<AppSettings>? configure = null)
        {
            Settings.Targets = new List<TargetConfig>
            {
                new() { Id = "gw", Name = "Gateway", Host = "192.168.0.1", IsGateway = true },
                new() { Id = "dns", Name = "DNS", Host = "example.test", Protocol = NetworkProtocol.Dns, IsDnsResolver = true },
                new() { Id = "ref", Name = "Reference", Host = "203.0.113.1" }
            };
            Settings.RecentWindowSeconds = 10;
            Settings.EventDedupSeconds = 30;
            configure?.Invoke(Settings);

            Engine = new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(),
                new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), Events)
            { Settings = Settings };
        }

        public static Measurement M(string id, int second, bool ok, double rtt = 20) => new()
        {
            TargetId = id,
            TargetName = id == "gw" ? "Gateway" : id == "dns" ? "DNS" : "Reference",
            TargetHost = "h",
            Timestamp = T0.AddSeconds(second),
            Success = ok,
            RttMs = ok ? rtt : 0,
            ErrorMessage = ok ? null : "timeout",
            Protocol = id == "dns" ? NetworkProtocol.Dns : NetworkProtocol.Icmp
        };

        public void Cycle(int second, bool gw = true, bool dns = true, bool reference = true, double refRtt = 20) =>
            Engine.IngestCycle(new[] { M("gw", second, gw, 1), M("dns", second, dns, 15), M("ref", second, reference, refRtt) });

        public TargetMetrics Metrics(string id) => Engine.GetCurrentMetrics().Single(m => m.TargetId == id);
        public IReadOnlyList<ConnectionEvent> Log(string type) => Events.GetRecentEvents(1000).Where(e => e.EventType == type).ToList();
    }

    [Fact]
    public void RecentLoss_RecoversWhileSessionLossKeepsHistory()
    {
        var rig = new Rig();
        for (int s = 0; s < 10; s++) rig.Cycle(s, reference: false);
        Assert.Equal(100.0, rig.Metrics("ref").PacketLossPercent);

        for (int s = 10; s < 30; s++) rig.Cycle(s);

        var m = rig.Metrics("ref");
        Assert.Equal(0.0, m.PacketLossPercent);                 // recent window has recovered
        Assert.Equal(10.0 / 30.0 * 100.0, m.SessionPacketLossPercent, 3); // history is still visible separately
    }

    [Fact]
    public void RecentJitter_DecaysAfterVariationLeavesTheWindow()
    {
        var rig = new Rig();
        for (int s = 0; s < 6; s++) rig.Cycle(s, refRtt: s % 2 == 0 ? 20 : 80);
        Assert.True(rig.Metrics("ref").Jitter > 30);

        for (int s = 6; s < 40; s++) rig.Cycle(s, refRtt: 20);

        Assert.Equal(0.0, rig.Metrics("ref").Jitter, 3);
    }

    [Fact]
    public void LatencySpike_UsesConfiguredThreshold()
    {
        var below = new Rig(s => s.LatencySpikeNotifyThreshold = 300);
        below.Cycle(0, refRtt: 20);
        below.Cycle(1, refRtt: 200); // +180 ms
        Assert.Empty(below.Log("LatencySpike"));

        var above = new Rig(s => s.LatencySpikeNotifyThreshold = 100);
        above.Cycle(0, refRtt: 20);
        above.Cycle(1, refRtt: 200);
        Assert.Single(above.Log("LatencySpike"));

        var disabled = new Rig(s => { s.LatencySpikeNotifyThreshold = 100; s.NotifyOnLatencySpike = false; });
        disabled.Cycle(0, refRtt: 20);
        disabled.Cycle(1, refRtt: 200);
        Assert.Empty(disabled.Log("LatencySpike"));
    }

    [Fact]
    public void RepeatedSpikes_AreDeduplicatedWithinWindow()
    {
        var rig = new Rig(s => s.LatencySpikeNotifyThreshold = 100);
        for (int s = 0; s < 20; s++) rig.Cycle(s, refRtt: s % 2 == 0 ? 20 : 200);

        Assert.Single(rig.Log("LatencySpike"));
    }

    [Fact]
    public void GatewayOutage_IsReportedOnceThenRestored()
    {
        var rig = new Rig();
        rig.Cycle(0);
        for (int s = 1; s <= 12; s++) rig.Cycle(s, gw: false);

        Assert.Single(rig.Log("GatewayDegraded"));
        Assert.DoesNotContain(rig.Log("Restored"), e => e.TargetName == "Gateway");

        rig.Cycle(13);
        var restored = rig.Log("Restored").Single(e => e.TargetName == "Gateway");
        Assert.Contains("12 s", restored.Description);
    }

    [Fact]
    public void SingleFailedGatewayProbe_DoesNotDeclareOutage()
    {
        var rig = new Rig();
        rig.Cycle(0);
        rig.Cycle(1, gw: false);
        rig.Cycle(2);

        Assert.Empty(rig.Log("GatewayDegraded"));
    }

    [Fact]
    public void DnsFailures_AreReportedOncePerOutage()
    {
        var rig = new Rig();
        for (int s = 0; s < 15; s++) rig.Cycle(s, dns: false);

        Assert.Single(rig.Log("DnsFailure"));
    }

    [Fact]
    public void SustainedProbeLoss_RaisesOneEventThenClears()
    {
        var rig = new Rig(s => { s.RecentWindowSeconds = 60; s.PacketLossNotifyThreshold = 5; });
        for (int s = 0; s < 12; s++) rig.Cycle(s, reference: s % 4 != 0); // 25% loss
        Assert.Single(rig.Log("PacketLoss"));
        Assert.Contains("probe", rig.Log("PacketLoss")[0].Description);

        for (int s = 12; s < 100; s++) rig.Cycle(s);

        Assert.Contains(rig.Log("Restored"), e => e.TargetName == "Reference");
    }

    [Fact]
    public void NoLossEvent_BelowMinimumSampleCount()
    {
        var rig = new Rig(s => s.RecentWindowSeconds = 60);
        for (int s = 0; s < 5; s++) rig.Cycle(s, reference: false);

        Assert.Empty(rig.Log("PacketLoss"));
    }

    [Fact]
    public void Disconnect_RequiresConsecutiveFullFailuresAndRestores()
    {
        var rig = new Rig();
        rig.Cycle(0);
        rig.Cycle(1, false, false, false);
        rig.Cycle(2, false, false, false);
        Assert.Empty(rig.Log("Disconnected")); // two bad cycles is not yet an outage

        rig.Cycle(3, false, false, false);
        var down = rig.Log("Disconnected").Single();
        Assert.Equal(T0.AddSeconds(1), down.Timestamp); // timestamped at outage onset

        rig.Cycle(4, false, false, false);
        Assert.Single(rig.Log("Disconnected"));

        rig.Cycle(5);
        Assert.Contains(rig.Log("Restored"), e => e.TargetName == SessionRecorder.ConnectivityScope);
    }

    [Fact]
    public void RecentLossDrivesDiagnosis_NotHistoricalLoss()
    {
        var rig = new Rig();
        // early burst of ISP-side loss on the reference target only...
        for (int s = 0; s < 10; s++) rig.Cycle(s, reference: false);
        // ...which has long since cleared
        for (int s = 10; s < 60; s++) rig.Cycle(s);

        Assert.Equal(0.0, rig.Engine.CurrentDiagnosis.PacketLossPercent);
    }

    [Fact]
    public void LatencyElevation_IsRelativeToTheTargetsOwnBaseline()
    {
        // A user whose normal latency to the reference is ~120 ms must not be flagged for 120 ms...
        var far = new Rig();
        for (int s = 0; s < 40; s++) far.Cycle(s, refRtt: 120);
        Assert.False(far.Metrics("ref").IsLatencyElevated);

        // ...but is flagged when latency doubles versus their own baseline.
        far.Cycle(40, refRtt: 400);
        Assert.True(far.Metrics("ref").IsLatencyElevated);
    }

    [Fact]
    public void SyntheticSession_IsRecordedEndToEnd()
    {
        var rig = new Rig();
        var recorder = new SessionRecorder();
        rig.Engine.MeasurementProcessed += (_, m) => recorder.RecordMeasurement(m);
        rig.Engine.CycleCompleted += (_, t) => recorder.RecordCycle(t);
        rig.Events.EventLogged += (_, e) => recorder.RecordEvent(e);
        recorder.Start(null, "synthetic", rig.Settings.Targets, null, 1000);

        for (int s = 0; s < 3; s++) rig.Cycle(s);
        for (int s = 3; s < 8; s++) rig.Cycle(s, false, false, false);
        for (int s = 8; s < 12; s++) rig.Cycle(s);

        var session = recorder.Stop()!;
        Assert.Equal(12 * 3, session.Samples.Count);
        Assert.Single(session.Disconnects);
        Assert.NotNull(session.Disconnects[0].EndUtc);
        Assert.Contains(session.Events, e => e.EventType == "Disconnected");
    }

    [Fact]
    public async Task Engine_StartStop_CancelsPromptly()
    {
        var rig = new Rig(s =>
        {
            s.Targets = new List<TargetConfig> { new() { Id = "lo", Name = "Loopback", Host = "127.0.0.1" } };
            s.MonitoringIntervalMs = 60_000; // would sleep a minute if cancellation were ignored
        });

        rig.Engine.Start();
        await Task.Delay(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Engine.Stop();

        Assert.False(rig.Engine.IsRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {sw.Elapsed}");
    }
}
