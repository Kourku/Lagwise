using Lagwise.Core.Statistics;

namespace Lagwise.Tests;

public class RollingWindowTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Samples_OlderThanWindow_AreExpired()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 20; i++) w.Add(T0.AddSeconds(i), true, 20);

        // window keeps the samples within the last 10 s of the newest one (inclusive boundary)
        Assert.InRange(w.Count, 10, 11);

        w.Prune(T0.AddSeconds(100));
        Assert.Equal(0, w.Count);
    }

    [Fact]
    public void Loss_RecoversOnceFailuresLeaveTheWindow()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 10; i++) w.Add(T0.AddSeconds(i), false, 0);
        Assert.Equal(100.0, w.LossPercent);

        for (int i = 10; i < 30; i++) w.Add(T0.AddSeconds(i), true, 25);

        Assert.Equal(0.0, w.LossPercent);
    }

    [Fact]
    public void Loss_ReflectsFailureRatioInsideWindow()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(60));
        for (int i = 0; i < 20; i++) w.Add(T0.AddSeconds(i), i % 5 != 0, 30);

        Assert.Equal(20, w.Count);
        Assert.Equal(4, w.FailureCount);
        Assert.Equal(20.0, w.LossPercent, 3);
    }

    [Fact]
    public void Jitter_IgnoresFailedProbesAndExpiredSamples()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(5));
        w.Add(T0, true, 10);
        w.Add(T0.AddSeconds(1), false, 0);
        w.Add(T0.AddSeconds(2), true, 30);   // |30-10| = 20
        Assert.Equal(20.0, w.JitterMs, 3);

        // a calm period later pushes the earlier variation out of the window
        for (int i = 10; i < 16; i++) w.Add(T0.AddSeconds(i), true, 30);
        Assert.Equal(0.0, w.JitterMs, 3);
    }

    [Fact]
    public void EmptyWindow_ReportsZeroes()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(5));
        Assert.Equal(0, w.LossPercent);
        Assert.Equal(0, w.JitterMs);
        Assert.Equal(0, w.MedianMs);
    }

    [Fact]
    public void NonPositiveDuration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow(TimeSpan.Zero));
    }
}
