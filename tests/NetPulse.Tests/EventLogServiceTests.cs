using NetPulse.Core.Services;

namespace NetPulse.Tests;

public class EventLogServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EventLogService Create(int dedupSeconds = 30) =>
        new() { DedupWindow = TimeSpan.FromSeconds(dedupSeconds) };

    [Fact]
    public void RecurringEvent_WithinWindow_IsLoggedOnce()
    {
        var log = Create();

        Assert.True(log.LogEvent("GatewayDegraded", "Critical", "failed", "Gateway", timestamp: T0));
        for (int i = 1; i <= 10; i++)
        {
            Assert.False(log.LogEvent("GatewayDegraded", "Critical", "failed", "Gateway", timestamp: T0.AddSeconds(i)));
        }

        Assert.Single(log.GetRecentEvents());
    }

    [Fact]
    public void RecurringEvent_AfterWindow_IsLoggedAgainAndReportsSuppressedCount()
    {
        var log = Create();
        log.LogEvent("DnsFailure", "Warning", "dns failed", "DNS", timestamp: T0);
        log.LogEvent("DnsFailure", "Warning", "dns failed", "DNS", timestamp: T0.AddSeconds(5));
        log.LogEvent("DnsFailure", "Warning", "dns failed", "DNS", timestamp: T0.AddSeconds(6));

        Assert.True(log.LogEvent("DnsFailure", "Warning", "dns failed", "DNS", timestamp: T0.AddSeconds(31)));

        var events = log.GetRecentEvents();
        Assert.Equal(2, events.Count);
        Assert.Contains("2 similar events suppressed", events[0].Description);
    }

    [Fact]
    public void SameType_DifferentTargets_AreNotCollapsed()
    {
        var log = Create();
        Assert.True(log.LogEvent("LatencySpike", "Warning", "spike", "Cloudflare", timestamp: T0));
        Assert.True(log.LogEvent("LatencySpike", "Warning", "spike", "Google", timestamp: T0.AddSeconds(1)));
        Assert.Equal(2, log.GetRecentEvents().Count);
    }

    [Fact]
    public void NonRecurringTypes_AreNeverSuppressed()
    {
        var log = Create();
        Assert.True(log.LogEvent("Restored", "Info", "back", "Gateway", timestamp: T0));
        Assert.True(log.LogEvent("Restored", "Info", "back", "Gateway", timestamp: T0.AddSeconds(1)));
        Assert.True(log.LogEvent("InterfaceChanged", "Info", "changed", timestamp: T0.AddSeconds(2)));
        Assert.Equal(3, log.GetRecentEvents().Count);
    }

    [Fact]
    public void ZeroDedupWindow_DisablesSuppression()
    {
        var log = Create(0);
        Assert.True(log.LogEvent("LatencySpike", "Warning", "a", "T", timestamp: T0));
        Assert.True(log.LogEvent("LatencySpike", "Warning", "a", "T", timestamp: T0.AddMilliseconds(10)));
    }

    [Fact]
    public void SuppressedEvents_DoNotRaiseEventLogged()
    {
        var log = Create();
        int raised = 0;
        log.EventLogged += (_, _) => raised++;

        log.LogEvent("PacketLoss", "Warning", "x", "T", timestamp: T0);
        log.LogEvent("PacketLoss", "Warning", "x", "T", timestamp: T0.AddSeconds(1));

        Assert.Equal(1, raised);
    }
}
