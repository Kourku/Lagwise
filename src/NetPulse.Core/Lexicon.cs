using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;

namespace NetPulse.Core;

/// <param name="Plain">One or two plain-language sentences for beginners.</param>
/// <param name="Detail">How NetPulse actually measures it and what it can and cannot tell you (shown in Advanced mode).</param>
public sealed record GlossaryEntry(string Key, string Term, string Plain, string Detail);

/// <summary>
/// The single source for the words NetPulse shows people: definitions, event labels and verdict names.
/// Keeping them here means the same thing is called the same thing everywhere, and tests can check coverage.
/// </summary>
public static class Lexicon
{
    public static IReadOnlyList<GlossaryEntry> Glossary { get; } = new List<GlossaryEntry>
    {
        new("latency", "Latency (ping)",
            "How long a small message takes to reach a server and come back, in milliseconds. Lower is better, but what counts as good depends on how far away the server is.",
            "Measured as round-trip time of probes (ICMP echo, TCP connect or HTTPS handshake). NetPulse never uses a single 'good ping' number: a target is judged against its own normal."),
        new("variation", "Latency variation (jitter)",
            "How much the ping bounces around from one reply to the next. Steady is better than low: big swings feel like stutter even when the average is fine.",
            "Shown as the average difference between consecutive replies over the recent window (60 s by default). The RFC 3550 smoothed jitter is a different, longer-memory number and is labelled separately."),
        new("loss", "Probe loss (packet loss)",
            "The share of NetPulse's test messages that got no reply during the last minute. A little is normal on some servers; sustained loss to several places is a real problem.",
            "Counts unanswered probes in the recent window, so it recovers when probes succeed again. It is loss of probes, not proof that game traffic was lost. Some servers ignore or rate-limit probes."),
        new("router", "Router (gateway)",
            "The box that connects your PC to the internet. If your router is slow or loses messages, the problem is inside your home network.",
            "The default gateway address reported by Windows. Routers sometimes answer pings with low priority, so a single odd router reading is weak evidence on its own."),
        new("reference", "Reference target",
            "A well-known public server (such as a public DNS service) that NetPulse pings to judge your connection in general. It is NOT your game's server.",
            "Reference targets are independent providers, so several of them degrading together is stronger evidence than one. Results describe your connection, never in-game latency."),
        new("endpoint", "Endpoint",
            "A network address that a program is talking to. A game talks to several (login, matchmaking, voice, downloads...), and only some of them carry the match.",
            "NetPulse labels every observed endpoint 'Unknown role' because nothing it can observe reliably says which one is the match server."),
        new("udp", "UDP and TCP",
            "Two ways programs send data. Most real-time games use UDP, which Windows does not let NetPulse see the other end of without special tools.",
            "GetExtendedUdpTable reports only the local port of a UDP socket. Seeing UDP peers would need elevated packet tracing, which NetPulse does not do."),
        new("icmp", "ICMP ping",
            "The classic 'ping'. Many servers ignore it on purpose, so no answer does not mean the server is down.",
            "Echo request/reply. Firewalls and cloud providers commonly filter or rate-limit it."),
        new("tcp-connect", "TCP connect time",
            "How long it takes to open a connection to a service. A way to measure servers that ignore ping.",
            "Times the TCP handshake to the endpoint's port. It measures that service, which may not be the machine running your match."),
        new("bufferbloat", "Bufferbloat",
            "Your connection getting laggy only when it is busy, for example during a big download, because your router queues data too long.",
            "NetPulse compares ping before and during saturating download/upload load from the test provider. Only the increase is graded, not the absolute ping. "
            + "It also watches real use: when this PC itself moves a lot of data, each reference server's ping during it is compared with its ping in the minute before. That is noted but never graded, because the line may not have been full."),
        new("route", "Route trace and hops",
            "The path your data takes: a list of the devices (hops) it passes through. A slow hop only matters if later hops are slow too.",
            "TTL-stepped ICMP probes. A hop that shows loss or delay while later hops and the destination look normal is usually just ignoring probes, so NetPulse does not blame it."),
        new("dns", "DNS lookup",
            "Turning a name like example.com into an address. Slow lookups make things feel slow to start even when the connection is fast.",
            "Measured with the system resolver and with direct queries to public resolvers."),
        new("baseline", "Your normal (baseline)",
            "What a target usually looks like for you. NetPulse flags a change from this, so someone far from the servers is not told that their normal ping is bad.",
            "The median of the target's recent history (needs at least 20 replies). Elevated means more than twice the baseline and at least 50 ms above it."),
        new("confidence", "Confidence",
            "How sure NetPulse is about a suggestion. It rises when several independent measurements agree and falls when they disagree.",
            "Low: one signal or contradicted. Medium: two independent signals. High: three or more agreeing signals and no contradiction."),
        new("session", "Session, gap and lag marker",
            "A session is one recording. A gap is a time when NetPulse itself was not measuring (for example the PC slept) and is not a network outage. A lag marker is you pressing 'Mark lag'.",
            "Sessions are stored only on this PC under %LOCALAPPDATA%\\NetPulse\\sessions."),
    };

    public static GlossaryEntry? Find(string key) => Glossary.FirstOrDefault(g => g.Key == key);

    /// <summary>Tooltip text: the plain definition, followed by the detail in Advanced mode.</summary>
    public static string Tip(string key, bool advanced = false)
    {
        var g = Find(key);
        if (g == null) return string.Empty;
        return advanced ? $"{g.Plain}\n\n{g.Detail}" : g.Plain;
    }

    private static readonly Dictionary<string, string> EventLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LatencySpike"] = "Latency spike",
        ["PacketLoss"] = "Probe loss",
        ["GatewayDegraded"] = "Router not answering",
        ["DnsFailure"] = "DNS lookups failing",
        ["RouteChange"] = "Route changed",
        ["InterfaceChanged"] = "Network adapter changed",
        ["NetworkChange"] = "Network changed",
        ["WifiRoam"] = "Wi-Fi access point changed",
        ["HeavyTraffic"] = "Heavy traffic on this PC",
        ["HeavyTrafficEnded"] = "Heavy traffic ended",
        ["Disconnected"] = "Connection lost",
        ["Restored"] = "Recovered",
        ["GameProcess"] = "Game",
        ["ConnectionChange"] = "Game connection",
        ["EndpointMeasurement"] = "Endpoint measurement",
        ["GameEndpointUnreachable"] = "Endpoint stopped answering",
        ["LagMarker"] = "Lag marked",
        ["MonitoringGap"] = "Monitoring paused",
        ["GameMonitor"] = "Game Monitor",
        ["BufferbloatTest"] = "Bufferbloat test",
        ["SpeedTest"] = "Speed test",
        ["EngineError"] = "Internal warning",
    };

    public static IReadOnlyCollection<string> KnownEventTypes => EventLabels.Keys;

    /// <summary>"45 s", "2 min 05 s", "1 h 20 min".</summary>
    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s" : $"{Math.Max(0, d.Seconds)} s";

    public static string EventLabel(string? eventType) =>
        eventType != null && EventLabels.TryGetValue(eventType, out var label) ? label : SplitCamelCase(eventType ?? string.Empty);

    public static string SeverityLabel(string? severity) => severity switch
    {
        "Critical" => "Problem",
        "Warning" => "Warning",
        _ => "Info"
    };

    /// <summary>Short verdict names for the Overview banner (the longer headline sits beneath it).</summary>
    public static string CategoryLabel(DiagnosticCategory category) => category switch
    {
        DiagnosticCategory.Optimal => "Healthy",
        DiagnosticCategory.Inconclusive => "No problem found",
        DiagnosticCategory.LocalNetworkIssue => "Possible local problem",
        DiagnosticCategory.IspInternetIssue => "Possible problem beyond your network",
        DiagnosticCategory.DnsIssue => "DNS lookups",
        DiagnosticCategory.TargetSpecificIssue => "One target affected",
        DiagnosticCategory.BufferbloatCongestion => "Slow under load",
        DiagnosticCategory.BandwidthExhausted => "Bandwidth used up",
        DiagnosticCategory.Disconnected => "Offline",
        _ => category.ToString()
    };

    private static string SplitCamelCase(string s)
    {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? s[i] : char.ToLowerInvariant(s[i]));
        }
        return sb.ToString();
    }
}
