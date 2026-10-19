using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

public enum CheckStatus { Good, Fair, Poor, Unknown }

public sealed record ReadinessItem(string Id, string Title, CheckStatus Status, string Detail);

public sealed record ReadinessResult(CheckStatus Overall, string Headline, IReadOnlyList<ReadinessItem> Items)
{
    public const string Scope =
        "Covers this PC, your home network and your internet connection, judged with your router and public reference servers. It cannot see the game's own servers.";
}

/// <summary>
/// "Am I good to play?" - a short checklist built from what is already being measured: the router, probe loss and ping
/// stability to the reference servers, latency against each server's own normal, DNS, Wi-Fi signal and the last
/// bufferbloat result. The limits are rules of thumb for real-time games and are stated in each item.
/// </summary>
public static class ReadinessCheck
{
    /// <summary>Recent probes a target needs before it is judged.</summary>
    public const int MinSamples = 8;

    /// <param name="lastRealLoad">Ping last seen during heavy traffic on this PC (real use, not a test), or null.</param>
    public static ReadinessResult Evaluate(IReadOnlyList<TargetMetrics> metrics, NetworkInterfaceInfo? nic, BufferbloatResult? lastBufferbloat, DateTime nowUtc,
        TestRecord? lastRealLoad = null)
    {
        bool wifi = nic?.IsWifi == true;
        var items = new List<ReadinessItem>
        {
            Router(metrics.FirstOrDefault(m => m.IsGateway), wifi),
        };

        var refs = metrics.Where(m => !m.IsGateway && !m.IsDnsResolver && !m.IsGameEndpoint && m.Protocol == NetworkProtocol.Icmp && m.RecentSampleCount >= MinSamples).ToList();
        items.Add(Loss(refs));
        items.Add(Stability(refs));
        items.Add(Normal(refs));
        items.Add(Dns(metrics.FirstOrDefault(m => m.IsDnsResolver)));
        if (wifi) items.Add(WifiSignal(nic!));
        items.Add(UnderLoad(lastBufferbloat, lastRealLoad, nowUtc));

        // an untested bufferbloat (or a missing DNS target) must not block "Good"; only measured items decide
        var decided = items.Where(i => i.Status != CheckStatus.Unknown).ToList();
        var core = items.Where(i => i.Id is "router" or "loss" or "stability").ToList();
        CheckStatus overall =
            core.All(i => i.Status == CheckStatus.Unknown) ? CheckStatus.Unknown
            : decided.Any(i => i.Status == CheckStatus.Poor) ? CheckStatus.Poor
            : decided.Any(i => i.Status == CheckStatus.Fair) ? CheckStatus.Fair
            : CheckStatus.Good;

        string headline = overall switch
        {
            CheckStatus.Good => "Good to play: nothing on your side of the connection looks wrong.",
            CheckStatus.Fair => "Playable, with something worth keeping an eye on.",
            CheckStatus.Poor => "Expect lag: start with the item marked in red.",
            _ => "Measuring... this takes a few seconds after NetPulse starts."
        };
        return new ReadinessResult(overall, headline, items);
    }

    private static ReadinessItem Router(TargetMetrics? gw, bool wifi)
    {
        const string title = "Your router";
        if (gw == null || gw.RecentSampleCount < MinSamples || gw.ReplyCount == 0)
            return new("router", title, CheckStatus.Unknown, gw == null ? "Your router has not been found yet." : "Waiting for enough replies from your router.");

        double latencyLimit = wifi ? 15 : 5, variationLimit = wifi ? 10 : 3;
        if (gw.PacketLossPercent >= 2)
            return new("router", title, CheckStatus.Poor,
                $"{gw.PacketLossPercent:F0}% of test messages to your router got no reply. Losses this close to you come from Wi-Fi, a cable or the router itself.");
        if (gw.MedianLatency > latencyLimit || gw.Jitter > variationLimit || gw.PacketLossPercent > 0)
            return new("router", title, CheckStatus.Fair,
                $"Router answers in {gw.MedianLatency:F1} ms typically, swinging by {gw.Jitter:F1} ms" + (gw.PacketLossPercent > 0 ? $", with {gw.PacketLossPercent:F1}% lost" : "")
                + $". Over {(wifi ? "Wi-Fi" : "a cable")} it is usually under {latencyLimit:F0} ms and steady.");
        return new("router", title, CheckStatus.Good, $"Answers in {gw.MedianLatency:F1} ms, steady ({gw.Jitter:F1} ms variation), nothing lost.");
    }

    private static ReadinessItem Loss(List<TargetMetrics> refs)
    {
        const string title = "Packet loss";
        if (refs.Count == 0) return new("loss", title, CheckStatus.Unknown, "Waiting for the reference servers to answer.");

        var lossy = refs.Where(r => r.PacketLossPercent >= 3).ToList();
        if (lossy.Count >= 2)
            return new("loss", title, CheckStatus.Poor,
                $"{lossy.Count} of {refs.Count} public servers are losing test messages ({string.Join(", ", lossy.Select(l => $"{l.TargetName} {l.PacketLossPercent:F0}%"))}). Several at once points at your connection, which games feel as rubber-banding.");
        if (lossy.Count == 1)
            return new("loss", title, CheckStatus.Good,
                $"Only {lossy[0].TargetName} is losing test messages ({lossy[0].PacketLossPercent:F0}%); the other servers are clean. One server alone usually just limits pings.");
        double worst = refs.Max(r => r.PacketLossPercent);
        return new("loss", title, CheckStatus.Good, worst > 0 ? $"Practically none (at most {worst:F1}% on one server)." : "None in the last minute.");
    }

    private static ReadinessItem Stability(List<TargetMetrics> refs)
    {
        const string title = "Ping stability";
        if (refs.Count == 0) return new("stability", title, CheckStatus.Unknown, "Waiting for the reference servers to answer.");

        double variation = StatisticalCalculator.CalculateMedian(refs.Select(r => r.Jitter).ToList());
        if (variation > 20)
            return new("stability", title, CheckStatus.Poor, $"Ping swings by about {variation:F0} ms between replies. Above ~20 ms, games feel jittery even when the average is fine.");
        if (variation > 8)
            return new("stability", title, CheckStatus.Fair, $"Ping swings by about {variation:F0} ms between replies. Under ~8 ms is ideal for fast games.");
        return new("stability", title, CheckStatus.Good, $"Steady: about {variation:F1} ms between replies.");
    }

    private static ReadinessItem Normal(List<TargetMetrics> refs)
    {
        const string title = "Compared with your normal";
        var known = refs.Where(r => r.BaselineLatencyMs > 0).ToList();
        if (known.Count == 0) return new("normal", title, CheckStatus.Unknown, "Learning what is normal for your connection (a few minutes after start-up).");

        var slow = known.Where(r => r.IsLatencyElevated).ToList();
        if (slow.Count >= 2)
            return new("normal", title, CheckStatus.Fair,
                $"{slow.Count} servers are much slower than their own normal right now ({string.Join(", ", slow.Select(s => $"{s.TargetName} {s.CurrentLatency:F0} vs {s.BaselineLatencyMs:F0} ms"))}). Something is loading your connection, or your provider is busy.");
        return new("normal", title, CheckStatus.Good, "Ping to the reference servers is at its usual level.");
    }

    private static ReadinessItem Dns(TargetMetrics? dns)
    {
        const string title = "DNS lookups";
        if (dns == null || dns.RecentSampleCount < 3 || dns.ReplyCount == 0) return new("dns", title, CheckStatus.Unknown, "Not measured yet.");
        if (dns.PacketLossPercent >= 20 || dns.MedianLatency > 200)
            return new("dns", title, CheckStatus.Fair, $"Name lookups are slow or failing ({dns.MedianLatency:F0} ms, {dns.PacketLossPercent:F0}% failed). This slows logging in and loading, not the match itself.");
        string speed = dns.MedianLatency < 1 ? "under 1 ms, answered from Windows' cache" : $"{dns.MedianLatency:F0} ms";
        return new("dns", title, CheckStatus.Good, $"Quick ({speed}). Only affects logging in and loading.");
    }

    private static ReadinessItem WifiSignal(NetworkInterfaceInfo nic)
    {
        const string title = "Wi-Fi signal";
        if (nic.WifiSignalPercent is not int signal) return new("wifi", title, CheckStatus.Unknown, "Windows did not report the signal strength.");
        if (signal < 35) return new("wifi", title, CheckStatus.Poor, $"Weak ({signal}%). Move closer to the router or use a cable; weak Wi-Fi causes loss and spikes.");
        if (signal < 60) return new("wifi", title, CheckStatus.Fair, $"Moderate ({signal}%). A cable, or being closer to the router, gives steadier ping.");
        return new("wifi", title, CheckStatus.Good, $"Strong ({signal}%). A cable is still steadier for competitive games.");
    }

    /// <summary>
    /// The notification shown when the selected game starts: the verdict, and the one or two items to look at
    /// (Windows shows about 200 characters).
    /// </summary>
    public static (string Title, string Message, bool Problem) GameStartNotice(string game, ReadinessResult r)
    {
        var watch = r.Items.Where(i => i.Status is CheckStatus.Poor or CheckStatus.Fair).OrderByDescending(i => i.Status).ToList();
        string title = r.Overall switch
        {
            CheckStatus.Good => $"{game} started: good to play",
            CheckStatus.Fair => $"{game} started: playable, one thing to watch",
            CheckStatus.Poor => $"{game} started: expect lag",
            _ => $"{game} started"
        };
        string message = r.Overall switch
        {
            CheckStatus.Unknown => "NetPulse is still measuring your connection. The Overview has the result in a few seconds.",
            CheckStatus.Good => "Nothing on your side of the connection looks wrong. NetPulse keeps watching while you play.",
            _ => string.Join(" ", watch.Take(2).Select(i => $"{i.Title}: {i.Detail}"))
        };
        if (message.Length > 200) message = message[..199] + "…";
        return (title, message, r.Overall is CheckStatus.Poor or CheckStatus.Fair);
    }

    /// <summary>Real-use observations older than this are not mentioned: the connection or router may have changed.</summary>
    public static readonly TimeSpan RealLoadRelevance = TimeSpan.FromDays(14);

    private static string Ago(TimeSpan age) =>
        age.TotalMinutes < 2 ? "just now" : age.TotalHours < 1 ? $"{age.TotalMinutes:F0} min ago" : age.TotalDays < 1 ? $"{age.TotalHours:F0} h ago" : $"{age.TotalDays:F0} days ago";

    private static ReadinessItem UnderLoad(BufferbloatResult? b, TestRecord? seen, DateTime nowUtc)
    {
        const string title = "Lag when others use the internet";
        if (seen != null && (seen.Kind != TestKind.RealLoad || nowUtc - seen.T > RealLoadRelevance)) seen = null;
        string seenText = seen == null ? "" : $"Seen in real use {Ago(nowUtc - seen.T)}: ping {(seen.RiseMs > PingUnderLoad.SteadyRiseMs ? $"rose by {seen.RiseMs:F0} ms" : "stayed steady")} while this PC downloaded at about {seen.DownMbps:F0} Mbps.";

        if (b == null || !b.IsValid)
        {
            // real use proves a problem when ping clearly rose; a steady ping proves little, the line may not have been full
            if (seen != null && seen.RiseMs > PingUnderLoad.ClearRiseMs)
                return new("load", title, seen.RiseMs > 60 ? CheckStatus.Poor : CheckStatus.Fair, seenText + " Run the Bufferbloat test for a grade under full load.");
            return new("load", title, CheckStatus.Unknown, seen != null
                ? seenText + " The line may not have been full: run the Bufferbloat test once for a grade."
                : "Not tested yet. Run the Bufferbloat test once to see whether downloads at home will make your game lag.");
        }
        var status = b.Grade switch { "A+" or "A" => CheckStatus.Good, "B" or "C" => CheckStatus.Fair, _ => CheckStatus.Poor };
        string text = $"Bufferbloat grade {b.Grade} (tested {Ago(nowUtc - b.Timestamp)}): ping rose by up to {Math.Max(b.DownloadDeltaMs, b.UploadDeltaMs):F0} ms under load.";
        // a newer real-use observation that went worse than the test is worth knowing
        if (seen != null && seen.T > b.Timestamp && seen.RiseMs > Math.Max(PingUnderLoad.ClearRiseMs, Math.Max(b.DownloadDeltaMs, b.UploadDeltaMs) + 15))
        {
            text += " " + seenText;
            if (status == CheckStatus.Good) status = CheckStatus.Fair;
        }
        return new("load", title, status, text);
    }
}
