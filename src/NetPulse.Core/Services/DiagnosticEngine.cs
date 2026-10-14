using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;

namespace NetPulse.Core.Services;

public class DiagnosticEngine
{
    public DiagnosticResult Analyze(
        NetworkInterfaceInfo? nic,
        TargetMetrics? gatewayMetrics,
        IReadOnlyList<TargetMetrics> internetMetrics,
        TargetMetrics? dnsMetrics,
        BufferbloatResult? bufferbloatResult = null)
    {
        var result = new DiagnosticResult
        {
            Timestamp = DateTime.UtcNow
        };

        var evidence = new List<string>();

        // Check 1: Interface state
        if (nic == null || nic.OperationalStatus.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            result.Category = DiagnosticCategory.Disconnected;
            result.Status = DiagnosticStatus.Critical;
            result.Headline = "This PC is not connected to a network.";
            result.LikelyCause = "Windows reports no working network adapter (cable unplugged, Wi-Fi off or disconnected).";
            result.RecommendedAction = "Check that Wi-Fi is on and connected, or that the network cable is plugged in at both ends.";
            evidence.Add("Network adapter status is reported as DOWN by Windows.");
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // Gather gateway stats
        double gwLatency = gatewayMetrics?.CurrentLatency ?? 0;
        double gwLoss = gatewayMetrics?.PacketLossPercent ?? 0;
        double gwJitter = gatewayMetrics?.Jitter ?? 0;
        bool gwReachable = gatewayMetrics?.IsReachable ?? false;

        result.GatewayLatencyMs = gwLatency;

        // Gather internet stats (excluding gateway and pure DNS)
        var validInternet = internetMetrics
            .Where(t => !t.IsGateway && !t.IsDnsResolver && t.TotalProbes > 0)
            .ToList();

        double inetAvgLatency = validInternet.Count > 0 ? validInternet.Average(t => t.CurrentLatency) : 0;
        double inetAvgLoss = validInternet.Count > 0 ? validInternet.Average(t => t.PacketLossPercent) : 0;
        double avgJitter = validInternet.Count > 0 ? validInternet.Average(t => t.Jitter) : gwJitter;

        result.InternetAverageLatencyMs = inetAvgLatency;
        result.PacketLossPercent = inetAvgLoss;
        result.AverageJitterMs = avgJitter;

        // Gather DNS stats
        double dnsLatency = dnsMetrics?.CurrentLatency ?? 0;
        double dnsLoss = dnsMetrics?.PacketLossPercent ?? 0;
        result.DnsLatencyMs = dnsLatency;

        // Evidence collection
        if (gatewayMetrics != null && gatewayMetrics.TotalProbes > 0)
        {
            evidence.Add(gwReachable
                ? $"• Your router answers in {gwLatency:F1} ms (variation {gwJitter:F1} ms, {gwLoss:F0}% lost)"
                : "• Your router is not answering (every recent probe was lost)");
        }

        if (validInternet.Count > 0)
        {
            int reaching = validInternet.Count(t => t.IsReachable);
            evidence.Add($"• Internet servers answering: {reaching} of {validInternet.Count} (average {inetAvgLatency:F1} ms, {inetAvgLoss:F0}% lost)");
        }

        if (dnsMetrics != null && dnsMetrics.TotalProbes > 0)
        {
            evidence.Add(dnsMetrics.IsReachable
                ? (dnsLatency < 1 ? "• Name lookups (DNS): under 1 ms, answered from Windows' cache" : $"• Name lookups (DNS): {dnsLatency:F1} ms")
                : "• Name lookups (DNS): failing, no answers");
        }

        if (nic.IsWifi && nic.WifiSignalPercent.HasValue)
        {
            evidence.Add($"• Wi-Fi signal: {nic.WifiSignalPercent.Value}% ({nic.WifiRadioType ?? "802.11"})");
        }

        // ====================================================
        // Rule 1: Disconnected from Router and Internet
        // ====================================================
        if (!gwReachable && validInternet.All(t => !t.IsReachable) && validInternet.Count > 0)
        {
            result.Category = DiagnosticCategory.Disconnected;
            result.Status = DiagnosticStatus.Critical;
            result.Headline = "Your router is not answering, so nothing gets through.";
            result.LikelyCause = "This PC cannot reach your home router, the first step of every connection.";
            result.RecommendedAction = "Check the Wi-Fi or cable between this PC and the router. If that looks fine, restart the router.";
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // ====================================================
        // Rule 2: Local Network / Wi-Fi / Router Issue
        // ====================================================
        bool wifiWeak = nic.IsWifi && nic.WifiSignalPercent.HasValue && nic.WifiSignalPercent.Value < 30;
        bool gwHighLatency = (nic.IsWifi && gwLatency > 25.0) || (!nic.IsWifi && gwLatency > 6.0);
        bool gwHighLoss = gwLoss > 4.0;

        if (gwHighLoss || gwHighLatency || wifiWeak)
        {
            result.Category = DiagnosticCategory.LocalNetworkIssue;
            result.Status = (gwLoss > 15.0 || gwLatency > 60.0) ? DiagnosticStatus.Critical : DiagnosticStatus.Degraded;
            result.Headline = "The problem looks like it is in your local network or Wi-Fi.";
            result.LikelyCause = wifiWeak
                ? "The Wi-Fi signal is weak, so packets are lost or delayed before they even reach your router."
                : "Replies from your router are slow or going missing, so the trouble starts between this PC and the router.";
            result.RecommendedAction = wifiWeak
                ? "Move closer to the router, or better, use a network cable for gaming."
                : "Check the cable or Wi-Fi between this PC and the router, or restart the router.";
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // ====================================================
        // Rule 3: DNS Specific Issue
        // ====================================================
        if (dnsMetrics != null && dnsMetrics.TotalProbes > 3 && (!dnsMetrics.IsReachable || dnsLoss > 20.0 || dnsLatency > 180.0))
        {
            if (gwReachable && gwLoss < 2.0 && validInternet.Any(t => t.IsReachable && t.PacketLossPercent < 5.0))
            {
                result.Category = DiagnosticCategory.DnsIssue;
                result.Status = DiagnosticStatus.Degraded;
                result.Headline = "Name lookups (DNS) are slow or failing.";
                result.LikelyCause = "The internet itself works, but turning names like a game's login server into addresses is slow or failing. This delays logging in and loading, not the match itself.";
                result.RecommendedAction = "Try a public DNS service such as Cloudflare (1.1.1.1) or Google (8.8.8.8) in Windows network settings, or restart the router.";
                evidence.Add($"• DNS resolution latency ({dnsLatency:F1} ms) is disproportionately higher than IP packet latency ({inetAvgLatency:F1} ms).");
                result.EvidenceItems = evidence;
                result.DetailSummary = string.Join("\n", evidence);
                return result;
            }
        }

        // ====================================================
        // Rule 4: ISP / Upstream Internet Degradation
        // ====================================================
        if (gwReachable && gwLoss <= 1.0 && gwLatency < 10.0)
        {
            // Router is pristine, but multiple Internet endpoints degrade
            int degradedCount = validInternet.Count(t => t.PacketLossPercent > 5.0 || t.IsLatencyElevated);
            if (degradedCount >= 2 || (validInternet.Count > 0 && degradedCount == validInternet.Count))
            {
                result.Category = DiagnosticCategory.IspInternetIssue;
                result.Status = inetAvgLoss > 10.0 ? DiagnosticStatus.Critical : DiagnosticStatus.Degraded;
                result.Headline = "Your home network is fine; the slowdown is at your internet provider or further out.";
                result.LikelyCause = "Your router answers quickly and reliably, but several independent internet servers are slow or losing packets at the same time.";
                result.RecommendedAction = "Nothing to fix at home. If it lasts or keeps coming back, export a report and show it to your internet provider.";
                result.EvidenceItems = evidence;
                result.DetailSummary = string.Join("\n", evidence);
                return result;
            }
        }

        // ====================================================
        // Rule 5: Target-Specific Degradation
        // ====================================================
        var failingTargets = validInternet.Where(t => t.TotalProbes > 3 && (!t.IsReachable || t.PacketLossPercent > 20.0 || t.CurrentLatency > (inetAvgLatency * 3.5))).ToList();
        if (failingTargets.Count == 1 && validInternet.Count >= 3)
        {
            var singleTarget = failingTargets[0];
            result.Category = DiagnosticCategory.TargetSpecificIssue;
            result.Status = DiagnosticStatus.Degraded;
            result.Headline = $"Only {singleTarget.TargetName} is having trouble; the rest of the internet is fine.";
            result.LikelyCause = $"{singleTarget.TargetName} is slow or losing packets while the other servers answer normally.";
            result.RecommendedAction = "Nothing to fix on your side: that server or its network is having a problem. Some servers also simply limit how often they answer pings.";
            evidence.Add($"• Target '{singleTarget.TargetName}' is degraded, while other endpoints are operating normally.");
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // ====================================================
        // Rule 6: Bufferbloat / Queue Congestion Check
        // ====================================================
        if (bufferbloatResult != null && (bufferbloatResult.Grade == "D" || bufferbloatResult.Grade == "F"))
        {
            result.Category = DiagnosticCategory.BufferbloatCongestion;
            result.Status = DiagnosticStatus.Degraded;
            result.Headline = "Ping rises a lot while the connection is busy (bufferbloat).";
            result.LikelyCause = "When something downloads or uploads, your router lets a long queue build up, and your game's packets wait in it.";
            result.RecommendedAction = "If your router has 'Smart Queue Management' (SQM, CAKE or fq_codel), turn it on. Otherwise avoid big downloads while you play.";
            evidence.Add($"• Bufferbloat Grade: {bufferbloatResult.Grade} (+{Math.Max(bufferbloatResult.DownloadDeltaMs, bufferbloatResult.UploadDeltaMs):F0} ms latency jump under load).");
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // ====================================================
        // Rule 7: Optimal Connection
        // ====================================================
        if (gwReachable && gwLatency <= 10.0 && gwLoss == 0 && inetAvgLoss == 0 && !validInternet.Any(t => t.IsLatencyElevated) && avgJitter < 8.0)
        {
            result.Category = DiagnosticCategory.Optimal;
            result.Status = DiagnosticStatus.Healthy;
            result.Headline = "Your connection looks healthy and steady.";
            result.LikelyCause = "Your router, name lookups and several independent internet servers all answer quickly and reliably.";
            result.RecommendedAction = "Nothing to do. NetPulse keeps watching in the background.";
            result.EvidenceItems = evidence;
            result.DetailSummary = string.Join("\n", evidence);
            return result;
        }

        // ====================================================
        // Rule 8: Inconclusive / Minor Fluctuations
        // ====================================================
        result.Category = DiagnosticCategory.Inconclusive;
        result.Status = DiagnosticStatus.Healthy;
        result.Headline = "Your connection is mostly steady, with small ups and downs.";
        result.LikelyCause = "Ping moves around a little, but not enough to point at any one cause. Brief wobbles like this are normal.";
        result.RecommendedAction = "Nothing to do right now. NetPulse keeps watching and will note anything that gets worse.";
        result.EvidenceItems = evidence;
        result.DetailSummary = string.Join("\n", evidence);
        return result;
    }
}
