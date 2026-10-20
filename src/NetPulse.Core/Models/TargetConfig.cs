using System;
using System.Collections.Generic;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Models;

public class TargetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public NetworkProtocol Protocol { get; set; } = NetworkProtocol.Icmp;
    public bool IsEnabled { get; set; } = true;
    public bool IsGateway { get; set; }
    public bool IsDnsResolver { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public string? Description { get; set; }
    /// <summary>What this target represents. Null means derive it (gateway / DNS / reference endpoint).</summary>
    public MeasurementKind? Kind { get; set; }
}

public class TargetMetrics
{
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string TargetHost { get; set; } = string.Empty;
    public NetworkProtocol Protocol { get; set; }
    public bool IsGateway { get; set; }
    public bool IsDnsResolver { get; set; }
    public MeasurementKind Kind { get; set; } = MeasurementKind.ReferenceEndpoint;
    /// <summary>Probes that received a reply (TotalProbes - FailedProbes).</summary>
    public int ReplyCount => TotalProbes - FailedProbes;

    public double CurrentLatency { get; set; }
    public double RollingAvg { get; set; }
    public double MinLatency { get; set; } = double.MaxValue;
    public double MaxLatency { get; set; }
    public double MedianLatency { get; set; }
    public double P95Latency { get; set; }
    /// <summary>Latency variation over the recent window (mean absolute difference of consecutive replies).</summary>
    public double Jitter { get; set; }
    /// <summary>RFC 3550 smoothed interarrival jitter (long memory; not windowed).</summary>
    public double SmoothedJitterRfc3550 { get; set; }
    /// <summary>Observed probe loss over the recent window only (see <see cref="RecentSampleCount"/>). Recovers when probes succeed again.</summary>
    public double PacketLossPercent { get; set; }
    /// <summary>Probe loss since monitoring started. Historical context, not current condition.</summary>
    public double SessionPacketLossPercent { get; set; }
    public int RecentSampleCount { get; set; }
    public double RecentWindowSeconds { get; set; }
    /// <summary>Typical (median) latency of this target over the history window; 0 until enough samples exist.</summary>
    public double BaselineLatencyMs { get; set; }

    /// <summary>True when current latency is well above this target's own typical latency. Never compares against a fixed number.</summary>
    public bool IsLatencyElevated =>
        BaselineLatencyMs > 0 && IsReachable && CurrentLatency > Math.Max(BaselineLatencyMs * 2.0, BaselineLatencyMs + 50.0);

    public int TotalProbes { get; set; }
    public int FailedProbes { get; set; }
    public DateTime LastUpdate { get; set; }
    public bool IsReachable { get; set; } = true;
    public string? LastError { get; set; }
    public HttpsTimingBreakdown? LastHttpsBreakdown { get; set; }

    public List<Measurement> RecentHistory { get; set; } = new();
}
