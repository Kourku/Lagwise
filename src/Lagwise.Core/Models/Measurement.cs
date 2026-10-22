using System;

namespace Lagwise.Core.Models;

public class HttpsTimingBreakdown
{
    public double DnsLookupMs { get; set; }
    public double TcpConnectMs { get; set; }
    public double TlsHandshakeMs { get; set; }
    public double TtfbMs { get; set; }
    public double TotalMs { get; set; }
    public int StatusCode { get; set; }

    public override string ToString() =>
        $"DNS: {DnsLookupMs:F1}ms | TCP: {TcpConnectMs:F1}ms | TLS: {TlsHandshakeMs:F1}ms | TTFB: {TtfbMs:F1}ms | Total: {TotalMs:F1}ms";

    /// <summary>"DNS cached · TCP 31 · TLS 32 · first byte 36 ms": a lookup under 1 ms was answered from Windows' cache, not "0 ms".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ShortText =>
        $"DNS {(DnsLookupMs < 1 ? "cached" : DnsLookupMs.ToString("F0"))} · TCP {TcpConnectMs:F0} · TLS {TlsHandshakeMs:F0} · first byte {TtfbMs:F0} ms";
}

public class Measurement
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string TargetHost { get; set; } = string.Empty;
    public NetworkProtocol Protocol { get; set; }
    public double RttMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public HttpsTimingBreakdown? HttpsBreakdown { get; set; }
    public string? ResolvedIp { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] {TargetName} ({Protocol}): {(Success ? $"{RttMs:F1} ms" : $"FAILED: {ErrorMessage}")}";
}
