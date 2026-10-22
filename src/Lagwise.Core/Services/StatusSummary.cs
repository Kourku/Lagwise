using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lagwise.Core.Models;

namespace Lagwise.Core.Services;

/// <summary>
/// A short plain-text snapshot of the connection for pasting into a chat, a forum post or a support ticket.
/// It deliberately leaves out IP addresses, adapter names and Wi-Fi network names, and it says plainly that the
/// numbers are reference-server measurements, not game-server ping.
/// </summary>
public static class StatusSummary
{
    public const int MaxServers = 6;

    /// <param name="historyLine">One line about the last days from the connection diary, or null.</param>
    public static string Build(ReadinessResult readiness, IReadOnlyList<TargetMetrics> metrics, NetworkInterfaceInfo? nic, DateTime localNow, string appVersion, string? historyLine = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Lagwise {appVersion} connection check, {localNow.ToString("d MMM yyyy HH:mm", inv)}");
        sb.AppendLine($"Verdict: {readiness.Headline}");
        sb.AppendLine($"Connection: {ConnectionText(nic)}");

        var gw = metrics.FirstOrDefault(m => m.IsGateway);
        if (gw != null && gw.ReplyCount > 0)
            sb.AppendLine(string.Format(inv, "Router: {0:0.#} ms typical, {1:0.#} ms variation, {2:0.#}% lost", gw.MedianLatency, gw.Jitter, gw.PacketLossPercent));

        var refs = metrics.Where(m => !m.IsGateway && !m.IsDnsResolver && m.TotalProbes > 0)
            .OrderBy(m => m.TargetName, StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Count > 0)
        {
            sb.AppendLine("Reference servers (not game servers):");
            foreach (var m in refs.Take(MaxServers))
            {
                if (m.ReplyCount == 0) { sb.AppendLine($"- {m.TargetName}: no replies"); continue; }
                string usual = m.BaselineLatencyMs > 0 ? string.Format(inv, " (usual {0:0})", m.BaselineLatencyMs) : "";
                sb.AppendLine(string.Format(inv, "- {0}: {1:0} ms now{2}, {3:0.#} ms variation, {4:0.#}% lost", m.TargetName, m.CurrentLatency, usual, m.Jitter, m.PacketLossPercent));
            }
            if (refs.Count > MaxServers) sb.AppendLine($"- and {refs.Count - MaxServers} more");
        }

        var dns = metrics.FirstOrDefault(m => m.IsDnsResolver);
        if (dns != null && dns.ReplyCount > 0)
            sb.AppendLine(string.Format(inv, "DNS lookups: {0:0} ms typical, {1:0}% failed", dns.MedianLatency, dns.PacketLossPercent));

        var marks = readiness.Items.Where(i => i.Status != CheckStatus.Unknown).Select(i => $"{Mark(i.Status)} {i.Title}").ToList();
        if (marks.Count > 0) sb.AppendLine("Checklist: " + string.Join(" | ", marks));
        foreach (var i in readiness.Items.Where(i => i.Status is CheckStatus.Poor or CheckStatus.Fair))
            sb.AppendLine($"  {i.Title}: {i.Detail}");

        if (!string.IsNullOrEmpty(historyLine)) sb.AppendLine(historyLine);
        sb.Append("Measured by Lagwise on this PC; it does not measure game-server ping.");
        return sb.ToString();
    }

    public static string Mark(CheckStatus s) => s switch
    {
        CheckStatus.Good => "OK",
        CheckStatus.Fair => "WATCH",
        CheckStatus.Poor => "PROBLEM",
        _ => "?"
    };

    private static string ConnectionText(NetworkInterfaceInfo? nic)
    {
        if (!NetworkChangeDescriber.IsConnected(nic)) return "no active connection";
        string link = LinkText(nic!);
        return nic!.TunnelName is { } vpn ? $"{link}, through a VPN or tunnel ({vpn})" : link;
    }

    private static string LinkText(NetworkInterfaceInfo nic)
    {
        if (nic.IsWifi)
        {
            var parts = new List<string> { "Wi-Fi" };
            if (nic.WifiSignalPercent is int s) parts.Add($"signal {s}%");
            if (!string.IsNullOrEmpty(nic.WifiRadioType)) parts.Add(nic.WifiRadioType!);
            return string.Join(", ", parts);
        }
        return nic.InterfaceType.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
            ? $"wired Ethernet{(nic.SpeedBps > 0 ? $", {nic.SpeedDisplay} link" : "")}"
            : nic.InterfaceType;
    }
}
