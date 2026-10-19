using NetPulse.Core.Services;

namespace NetPulse.Tests;

public class AdapterActivityTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>A fake adapter whose counters the test advances.</summary>
    private sealed class Fake
    {
        public long Rx, Tx, PRx, PTx, Errors, Discards;
        public bool Present = true;
        public long Speed = 1_000_000_000;
        public IReadOnlyList<AdapterCounters> Read() => Present
            ? new[] { new AdapterCounters("{eth}", "Ethernet", "Test NIC", "Ethernet", Speed, Rx, Tx, PRx, PTx, Errors, Discards) }
            : Array.Empty<AdapterCounters>();
    }

    [Fact]
    public void Speeds_AreTheCounterDifferencesPerSecond_InBits()
    {
        var f = new Fake();
        var m = new AdapterActivityMonitor(f.Read);
        m.Sample(T0);                       // baseline only
        Assert.Empty(m.Snapshot().Single().History);

        f.Rx += 1_250_000; f.Tx += 125_000; f.PRx += 1000; f.PTx += 400;   // 10 Mbps down, 1 Mbps up
        m.Sample(T0.AddSeconds(1));
        var a = m.Snapshot().Single();
        var p = a.Latest!;
        Assert.Equal(10_000_000, p.RxBps, 3);
        Assert.Equal(1_000_000, p.TxBps, 3);
        Assert.Equal(1000, p.RxPps, 3);
        Assert.Equal(1_250_000, a.BytesReceivedSinceStart);
        Assert.Equal(0.01, a.Utilization!.Value, 6);
    }

    [Fact]
    public void ACounterThatGoesBackwards_IsAReset_NotANegativeSpeed()
    {
        var f = new Fake { Rx = 5_000_000, Tx = 5_000_000 };
        var m = new AdapterActivityMonitor(f.Read);
        m.Sample(T0);
        f.Rx = 100; f.Tx = 100;             // adapter re-enabled
        m.Sample(T0.AddSeconds(1));
        f.Rx += 125_000; f.Tx += 0;
        m.Sample(T0.AddSeconds(2));

        var a = m.Snapshot().Single();
        Assert.All(a.History, h => Assert.True(h.RxBps >= 0 && h.TxBps >= 0));
        Assert.Equal(1_000_000, a.Latest!.RxBps, 3);
        Assert.Equal(125_000, a.BytesReceivedSinceStart);   // counted again from the reset
    }

    [Fact]
    public void HistoryIsKeptForFifteenMinutes_AndPeaksFollowTheWindow()
    {
        var f = new Fake();
        var m = new AdapterActivityMonitor(f.Read);
        m.Sample(T0);
        for (int s = 1; s <= 20 * 60; s++)
        {
            f.Rx += s == 30 ? 12_500_000 : 12_500;   // one 100 Mbps second early on, otherwise 100 Kbps
            m.Sample(T0.AddSeconds(s));
        }
        var a = m.Snapshot().Single();
        var now = a.Latest!.T;
        Assert.True(a.History.Count <= 15 * 60 + 1);
        Assert.Equal(100_000, a.PeakRx(TimeSpan.FromMinutes(1), now), 3);
        Assert.Equal(100_000, a.AverageRx(TimeSpan.FromMinutes(1), now), 3);
    }

    [Fact]
    public void SustainedHeavyTraffic_IsReportedOncePerStretch()
    {
        var f = new Fake();
        var m = new AdapterActivityMonitor(f.Read) { HeavyTrafficBps = 15_000_000, HeavyTrafficAfter = TimeSpan.FromSeconds(10) };
        var reports = new List<HeavyTraffic>();
        m.HeavyTrafficDetected += (_, h) => reports.Add(h);
        m.Sample(T0);

        int s = 0;
        void Run(int seconds, long bytesPerSecond) { for (int i = 0; i < seconds; i++) { f.Rx += bytesPerSecond; m.Sample(T0.AddSeconds(++s)); } }

        Run(5, 5_000_000);       // 40 Mbps for 5 s: too short
        Run(6, 10_000);          // quiet again, for longer than a dip
        Assert.Empty(reports);

        Run(20, 5_000_000);      // 40 Mbps for 20 s
        var r = Assert.Single(reports);
        Assert.Equal("Ethernet", r.Adapter);
        Assert.Equal(40_000_000, r.RxBps, 3);
        Assert.True(r.Duration >= TimeSpan.FromSeconds(10));

        Run(6, 10_000);          // drops back for longer than a dip, then a new stretch is a new report
        Run(12, 5_000_000);
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public void NetPulsesOwnTests_AreNotReportedAsHeavyTraffic()
    {
        var f = new Fake();
        bool testing = true;
        var m = new AdapterActivityMonitor(f.Read) { SuppressHeavyTraffic = () => testing };
        int reports = 0;
        m.HeavyTrafficDetected += (_, _) => reports++;
        m.Sample(T0);
        for (int s = 1; s <= 30; s++) { f.Rx += 50_000_000; m.Sample(T0.AddSeconds(s)); }
        Assert.Equal(0, reports);
    }

    [Fact]
    public void ErrorsAndDrops_CountOnlySinceNetPulseStarted()
    {
        var f = new Fake { Errors = 40, Discards = 7 };   // already counted by Windows before NetPulse started
        var m = new AdapterActivityMonitor(f.Read);
        m.Sample(T0);
        f.Errors += 2; f.Discards += 3;
        m.Sample(T0.AddSeconds(1));
        var a = m.Snapshot().Single();
        Assert.Equal(2, a.ErrorsSinceStart);
        Assert.Equal(3, a.DiscardsSinceStart);
    }

    [Fact]
    public void AnAdapterThatDisappears_IsForgotten()
    {
        var f = new Fake();
        var m = new AdapterActivityMonitor(f.Read);
        m.Sample(T0);
        f.Present = false;
        m.Sample(T0.AddSeconds(1));
        Assert.Empty(m.Snapshot());
    }

    [Fact]
    public void AReadingThatThrows_IsSkipped()
    {
        var m = new AdapterActivityMonitor(() => throw new InvalidOperationException("adapter vanished"));
        m.Sample(T0);
        Assert.Empty(m.Snapshot());
    }

    [Theory]
    [InlineData(0, "0 bps")]
    [InlineData(850_000, "850 Kbps")]
    [InlineData(2_340_000, "2.3 Mbps")]
    [InlineData(412_000_000, "412 Mbps")]
    [InlineData(1_200_000_000, "1.2 Gbps")]
    public void SpeedsAreFormattedReadably(double bps, string expected) =>
        Assert.Equal(expected, AdapterActivityMonitor.FormatBps(bps).Replace(',', '.'));
}
