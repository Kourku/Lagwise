using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lagwise.Core.Models;

namespace Lagwise.Core.Services;

public class StorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task ExportToCsvAsync(string filePath, IReadOnlyList<Measurement> measurements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUTC,TargetId,TargetName,TargetHost,Protocol,Success,RttMs,ResolvedIp,Error,HttpsDetails");

        foreach (var m in measurements)
        {
            // every text field quoted, and nothing a spreadsheet would run as a formula
            string httpsInfo = m.HttpsBreakdown != null ? UntrustedText.CsvField(m.HttpsBreakdown.ToString()) : "";
            string error = !string.IsNullOrEmpty(m.ErrorMessage) ? UntrustedText.CsvField(m.ErrorMessage) : "";

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:O},{1},{2},{3},{4},{5},{6:F2},{7},{8},{9}",
                m.Timestamp,
                UntrustedText.CsvField(m.TargetId),
                UntrustedText.CsvField(m.TargetName),
                UntrustedText.CsvField(m.TargetHost),
                m.Protocol,
                m.Success,
                m.RttMs,
                UntrustedText.CsvField(m.ResolvedIp),
                error,
                httpsInfo));
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }

    public async Task ExportToJsonAsync(string filePath, object data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
    }

    /// <summary>
    /// The connection report (Export report, Ctrl+E): what is measured right now, the latest tests and their history,
    /// and the event timeline, in local time and plain words, for a provider's support or a forum.
    /// </summary>
    /// <param name="tests">Stored test results, or null.</param>
    /// <param name="toLocal">Converts UTC to the time shown; the PC's local time by default.</param>
    public Task<string> GenerateDiagnosticReportAsync(
        NetworkInterfaceInfo nic,
        DiagnosticResult diagnosis,
        IReadOnlyList<TargetMetrics> targetMetrics,
        IReadOnlyList<ConnectionEvent> events,
        RoutePath? routePath = null,
        BufferbloatResult? bufferbloat = null,
        SpeedTestResult? speedTest = null,
        IReadOnlyList<TestRecord>? tests = null,
        Func<DateTime, DateTime>? toLocal = null,
        IReadOnlyList<string>? diaryLines = null,
        IReadOnlyList<Sites.SiteResult>? sites = null,
        bool includeAddresses = true)
    {
        var local = toLocal ?? (t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime());
        string Clock(DateTime utc) => local(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);

        sb.AppendLine("# Lagwise Network Quality & Diagnostic Report");
        sb.AppendLine();
        sb.AppendLine($"Created {Clock(now)} (local time, UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}) by Lagwise {AppInfo.Version}.");
        sb.AppendLine("The reference servers are public servers, not game servers: nothing in this report is in-game ping. "
            + (includeAddresses
                ? "It contains this PC's local network addresses, so share it only with people you trust, such as your provider's support."
                : "Addresses on your home network and IPv6 addresses read [hidden]; Settings > Privacy and data can include them for your provider's support."));
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Verdict:** {Lexicon.CategoryLabel(diagnosis.Category)}. {diagnosis.Headline}");
        if (!string.IsNullOrWhiteSpace(diagnosis.LikelyCause)) sb.AppendLine($"- **Likely cause:** {diagnosis.LikelyCause}");
        if (!string.IsNullOrWhiteSpace(diagnosis.RecommendedAction)) sb.AppendLine($"- **Next step:** {diagnosis.RecommendedAction}");
        sb.AppendLine();

        sb.AppendLine("## Active Network Interface");
        sb.AppendLine();
        sb.AppendLine($"- **Adapter:** {nic.Name} ({nic.Description})");
        sb.AppendLine($"- **Type:** {(nic.IsWifi ? "Wi-Fi" : nic.InterfaceType)}, {nic.OperationalStatus}, link speed {nic.SpeedDisplay}");
        sb.AppendLine($"- **IPv4:** {nic.Ipv4Address} (mask {nic.Ipv4Subnet}), router {nic.Ipv4Gateway}");
        if (!string.IsNullOrEmpty(nic.Ipv6Address))
            sb.AppendLine($"- **IPv6:** {nic.Ipv6Address}{(string.IsNullOrEmpty(nic.Ipv6Gateway) ? "" : $", router {nic.Ipv6Gateway}")}");
        sb.AppendLine($"- **DNS servers:** {(nic.DnsServers.Count > 0 ? string.Join(", ", nic.DnsServers) : "none reported")}");
        sb.AppendLine($"- **To the internet:** {(nic.TunnelName is { } vpn ? $"through a VPN or tunnel, {vpn}; internet numbers include its detour" : "direct, through this adapter")}");
        if (nic.IsWifi)
        {
            // the network name and the access point's address identify a place, and a provider does not need them
            sb.AppendLine($"- **Wi-Fi:** signal {(nic.WifiSignalPercent.HasValue ? $"{nic.WifiSignalPercent}%" : "unknown")}, {nic.WifiRadioType ?? "radio type unknown"}, "
                + $"channel {(nic.WifiChannel.HasValue ? nic.WifiChannel.Value.ToString(CultureInfo.InvariantCulture) : "unknown")}");
        }
        sb.AppendLine();

        sb.AppendLine("## Real-Time Probing Statistics");
        sb.AppendLine();
        sb.AppendLine("Over the recent window. Variation is the average change from one reply to the next; loss counts unanswered probes, which some servers ignore on purpose.");
        sb.AppendLine();
        sb.AppendLine("| Server | Method | Now (ms) | Median (ms) | Min (ms) | Max (ms) | P95 (ms) | Variation (ms) | Loss | Probes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var t in targetMetrics)
        {
            string method = t.Protocol switch { NetworkProtocol.Icmp => "ICMP", NetworkProtocol.Tcp => "TCP", NetworkProtocol.Https => "HTTPS", NetworkProtocol.Dns => "DNS", _ => t.Protocol.ToString() };
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} | {2:F1} | {3:F1} | {4:F1} | {5:F1} | {6:F1} | {7:F1} | {8:F1}% | {9} |",
                t.TargetName, method, t.CurrentLatency, t.MedianLatency, t.MinLatency == double.MaxValue ? 0 : t.MinLatency,
                t.MaxLatency, t.P95Latency, t.Jitter, t.PacketLossPercent, t.TotalProbes));
        }
        sb.AppendLine();

        if (routePath != null && routePath.Hops.Count > 0)
        {
            sb.AppendLine($"## Route Trace to {routePath.Destination}");
            sb.AppendLine();
            sb.AppendLine($"{routePath.HopCount} hops, {routePath.TotalLatencyMs:F1} ms to the last hop that answered. A hop that is slow or silent only matters when the hops after it are too.");
            sb.AppendLine();
            sb.AppendLine("| Hop | Address | Name | Round trip (ms) | Loss | Network | Note |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var h in routePath.Hops)
            {
                string note = h.IsPersistentLatencyIncrease ? "Lasting rise from here on" : !h.IsResponding ? "No reply (often ignores probes)" : "";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:F1} | {4:F0}% | {5} | {6} |",
                    h.HopNumber, h.IpAddress, h.DisplayHost, h.CurrentRttMs, h.LossPercent, h.AsDisplay, note));
            }
            sb.AppendLine();
        }

        if (bufferbloat != null && bufferbloat.IsValid)
        {
            sb.AppendLine("## Latest Bufferbloat Test");
            sb.AppendLine();
            sb.AppendLine($"- **Grade {bufferbloat.Grade}**, tested {Clock(bufferbloat.Timestamp)}: {bufferbloat.Summary}");
            sb.AppendLine($"- **Ping at rest:** {bufferbloat.BaselineLatencyMs:F1} ms");
            sb.AppendLine($"- **While downloading:** {bufferbloat.DownloadLoadedLatencyMs:F1} ms (+{bufferbloat.DownloadDeltaMs:F1} ms){(bufferbloat.DownloadLoadMbps > 0 ? $", at {bufferbloat.DownloadLoadMbps:F0} Mbps" : "")}");
            sb.AppendLine($"- **While uploading:** {bufferbloat.UploadLoadedLatencyMs:F1} ms (+{bufferbloat.UploadDeltaMs:F1} ms){(bufferbloat.UploadLoadMbps > 0 ? $", at {bufferbloat.UploadLoadMbps:F0} Mbps" : "")}");
            if (!string.IsNullOrWhiteSpace(bufferbloat.Explanation)) sb.AppendLine($"- {bufferbloat.Explanation}");
            if (!string.IsNullOrWhiteSpace(bufferbloat.Recommendation)) sb.AppendLine($"- **Advice:** {bufferbloat.Recommendation}");
            sb.AppendLine();
        }

        if (speedTest != null && speedTest.CompletedSuccessfully)
        {
            sb.AppendLine("## Latest Speed Test");
            sb.AppendLine();
            sb.AppendLine($"- **Download:** {speedTest.DownloadSpeedMbps:F1} Mbps ({speedTest.BytesDownloaded / 1_000_000.0:F1} MB)");
            sb.AppendLine($"- **Upload:** {speedTest.UploadSpeedMbps:F1} Mbps ({speedTest.BytesUploaded / 1_000_000.0:F1} MB)");
            sb.AppendLine($"- Tested {Clock(speedTest.Timestamp)}{(string.IsNullOrEmpty(speedTest.Provider) ? "" : $" against {speedTest.Provider}")}, {speedTest.DurationSeconds:F0} s.");
            sb.AppendLine();
        }

        if (tests != null && tests.Count > 0)
        {
            sb.AppendLine("## Tests Over Time");
            sb.AppendLine();
            var trends = new[]
            {
                TestTrends.Speed(tests.Where(t => t.Kind == TestKind.Speed).ToList(), local),
                TestTrends.Bufferbloat(tests.Where(t => t.Kind == TestKind.Bufferbloat).ToList(), local),
                TestTrends.RealLoad(tests.Where(t => t.Kind == TestKind.RealLoad).ToList())
            };
            foreach (var line in trends.Where(l => l.Length > 0)) sb.AppendLine($"- {line}");
            sb.AppendLine();
            sb.AppendLine("| When (local) | What | Connection | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var t in tests.OrderByDescending(t => t.T).Take(30))
            {
                string what = t.Kind switch { TestKind.Speed => "Speed test", TestKind.Bufferbloat => "Bufferbloat test", _ => "Seen in real use" };
                string result = t.Kind switch
                {
                    TestKind.Speed => string.Format(CultureInfo.InvariantCulture, "{0:F1} Mbps down, {1:F1} Mbps up", t.DownMbps, t.UpMbps),
                    TestKind.Bufferbloat => string.Format(CultureInfo.InvariantCulture, "grade {0}: +{1:F0} ms downloading, +{2:F0} ms uploading ({3:F0} ms at rest)", t.Grade, t.DownDeltaMs, t.UpDeltaMs, t.RestMs),
                    _ => string.Format(CultureInfo.InvariantCulture, "ping {0:+0;-0;0} ms ({1:F0} to {2:F0} ms) during {3} of heavy traffic at about {4:F0} Mbps down",
                        t.RiseMs, t.RestMs, t.LoadedMs, Lexicon.Duration(TimeSpan.FromSeconds(t.Seconds)), t.DownMbps)
                };
                sb.AppendLine($"| {local(t.T).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} | {what} | {(t.Connection.Length > 0 ? t.Connection : "-")} | {result} |");
            }
            sb.AppendLine();
        }

        if (diaryLines != null && diaryLines.Count > 0)
        {
            sb.AppendLine("## Connection Diary (last 14 days)");
            sb.AppendLine();
            sb.AppendLine("Hour by hour while Lagwise was running, recording or not. Ping above usual compares each reference server with its own usual; a drop is a stretch when nothing on the internet answered.");
            sb.AppendLine();
            foreach (var line in diaryLines) sb.AppendLine(line);
            sb.AppendLine();
        }

        if (sites != null && sites.Count > 0)
        {
            sb.AppendLine("## Site Check");
            sb.AppendLine();
            sb.AppendLine($"Checked {Clock(sites.Max(s => s.CheckedUtc))}. Each site was visited from this PC step by step (name lookup with your DNS and with public DNS, connection, secure handshake, answer).");
            sb.AppendLine();
            sb.AppendLine("| Site | Result | What was measured |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in sites)
                sb.AppendLine($"| {r.Site.Name} ({r.Site.Host}) | {r.Headline} | {r.Explanation.Replace("|", "/")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Event Timeline");
        sb.AppendLine();
        if (events.Count == 0)
        {
            sb.AppendLine("No events were recorded since Lagwise started.");
        }
        else
        {
            if (events.Count > 50) sb.AppendLine($"The latest 50 of {events.Count} events, newest first.");
            foreach (var e in events.OrderByDescending(e => e.Timestamp).Take(50))
                sb.AppendLine($"- `{Clock(e.Timestamp)}` **{Lexicon.SeverityLabel(e.Severity)}**, {Lexicon.EventLabel(e.EventType)}: {e.Description}");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine("*Made by Lagwise on this PC. Lagwise sends nothing anywhere; this file only leaves the PC if you share it.*");

        return Task.FromResult(includeAddresses ? sb.ToString() : ReportPrivacy.HideAddresses(sb.ToString()));
    }
}
