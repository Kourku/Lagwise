using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NetPulse.Core.Games;

namespace NetPulse.Core.Sessions;

/// <summary>
/// Builds the written report for a recorded session. Everything is derived from the local session file; nothing is sent anywhere.
/// It starts with the verdict (was it the player's connection?) and never presents a reference measurement as game-server ping.
/// </summary>
public static class SessionReportBuilder
{
    private static string Ms(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) + " ms" : "-";
    private static string Local(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss");
    private static string Clock(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("HH:mm:ss");
    private static string Dur(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{d.Seconds} s";

    public static string Rating(VerdictLevel l) => l switch
    {
        VerdictLevel.Good => "Good",
        VerdictLevel.Fair => "Some trouble",
        VerdictLevel.Poor => "Poor",
        _ => "Not judged"
    };

    public static string BuildMarkdown(MonitoringSession s, SessionStats stats, double busyBps = SessionAnalysis.DefaultBusyBps)
    {
        var verdict = SessionVerdict.Build(s, busyBps);
        var sb = new StringBuilder();
        sb.AppendLine($"# NetPulse session report: {s.Name}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(s.GameDisplayName))
            sb.AppendLine($"- **Game:** {s.GameDisplayName}");
        sb.AppendLine($"- **Recorded:** {Local(s.StartedUtc)} to {(s.EndedUtc.HasValue ? Local(s.EndedUtc.Value) : "(recording was interrupted)")} (local time)");
        sb.AppendLine($"- **Length:** {Dur(stats.Duration)}{(s.IsComplete ? "" : " (incomplete: the recording ended without a normal stop, so data up to the last checkpoint is shown)")}");
        sb.AppendLine($"- **Connection:** {s.ActiveInterface ?? "unknown"}{(string.IsNullOrEmpty(s.InterfaceType) ? "" : $" ({s.InterfaceType})")}");
        sb.AppendLine($"- **Probes recorded:** {stats.ProbeCount:N0}{(s.SamplesTruncated ? " (sample cap reached; later samples were not stored)" : "")}");
        sb.AppendLine($"- **Events:** {s.Events.Count}, **lag markers:** {s.LagMarkers.Count}, **monitoring gaps:** {s.Gaps.Count}, **connection losses:** {s.Disconnects.Count}");
        sb.AppendLine();
        sb.AppendLine("> These are measurements of your connection (your router and reference servers). They are **not** the ping shown inside a game, and a reference server is not the game's server.");
        sb.AppendLine();

        sb.AppendLine($"## Verdict: {verdict.Headline}");
        sb.AppendLine();
        sb.AppendLine(verdict.Summary);
        sb.AppendLine();
        sb.AppendLine("| Part of the connection | Rating | What NetPulse saw |");
        sb.AppendLine("|---|---|---|");
        foreach (var l in verdict.Layers) sb.AppendLine($"| {l.Name} | {Rating(l.Level)} | {l.Text.Replace("|", "/")} |");
        sb.AppendLine();
        if (verdict.Episodes.Count > 0)
        {
            sb.AppendLine("### When it happened");
            sb.AppendLine();
            foreach (var e in verdict.Episodes.Take(40)) sb.AppendLine($"- {e.Text}");
            if (verdict.Episodes.Count > 40) sb.AppendLine($"- ...and {verdict.Episodes.Count - 40} more.");
            sb.AppendLine();
        }
        sb.AppendLine("### Moments you marked as laggy");
        sb.AppendLine();
        if (verdict.LagMarks.Count == 0)
            sb.AppendLine("None. Pressing **Mark lag** (or its keyboard shortcut) when you notice lag lets this report say what your connection did at that moment.");
        foreach (var (lag, i) in verdict.LagMarks.Select((l, i) => (l, i)))
        {
            sb.AppendLine($"- **{Clock(lag.T)}{(string.IsNullOrEmpty(lag.Note) ? "" : $" ({lag.Note})")}: {lag.Headline}.** {lag.Detail}");
            if (i >= stats.LagCorrelations.Count) continue;
            if (stats.LagCorrelations[i].Traffic is { } traffic) sb.AppendLine($"  - {traffic.Describe()}");
            foreach (var e in stats.LagCorrelations[i].NearbyEvents.Take(4))
                sb.AppendLine($"  - {Clock(e.Timestamp)} {Lexicon.EventLabel(e.EventType)}: {e.Description}");
        }
        sb.AppendLine();
        if (verdict.Advice.Count > 0)
        {
            sb.AppendLine("### What to do");
            sb.AppendLine();
            foreach (var a in verdict.Advice) sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        sb.AppendLine("## What was measured");
        sb.AppendLine();
        sb.AppendLine("| Target | What it is | Method | Address |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var t in s.Targets)
        {
            var stat = stats.Targets.First(x => x.TargetId == t.Id);
            sb.AppendLine($"| {t.Name} | {stat.KindLabel} | {stat.Method} | {t.Host} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Latency, variation and probe loss");
        sb.AppendLine();
        sb.AppendLine("| Target | Probes | Replies | Loss | Min | Median | 95th pct | Max | Variation | Longest run without reply |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var t in stats.Targets)
            sb.AppendLine($"| {t.Name} | {t.Probes:N0} | {t.Replies:N0} | {t.LossPercent:F1}% | {Ms(t.MinMs)} | {Ms(t.MedianMs)} | {Ms(t.P95Ms)} | {Ms(t.MaxMs)} | {Ms(t.VariationMs)} | {t.LongestFailureStreak} |");
        sb.AppendLine();
        sb.AppendLine("*Loss* is the share of probes that got no reply. *Variation* is the average difference between consecutive replies. A target with high loss or variation for the whole session is usually just unsteady (for example a public resolver rate-limiting pings), not a sign of a new problem.");
        sb.AppendLine();

        sb.AppendLine("## Gaps and outages");
        sb.AppendLine();
        if (s.Gaps.Count == 0 && s.Disconnects.Count == 0)
        {
            sb.AppendLine("No monitoring gaps and no connection losses were recorded.");
        }
        else
        {
            foreach (var g in s.Gaps)
                sb.AppendLine($"- **Monitoring gap** {Clock(g.StartUtc)}-{Clock(g.EndUtc)} ({g.DurationSeconds:F0} s): NetPulse was not measuring (for example the PC slept). This is not a network outage.");
            foreach (var d in s.Disconnects)
                sb.AppendLine($"- **Connection lost** from {Clock(d.StartUtc)}{(d.EndUtc.HasValue ? $" to {Clock(d.EndUtc.Value)} ({d.DurationSeconds:F0} s)" : " (still down when the recording ended)")}: no probe to any target succeeded.");
        }
        sb.AppendLine();

        sb.AppendLine("## Events");
        sb.AppendLine();
        var events = s.Events.OrderBy(e => e.Timestamp).ToList();
        if (events.Count == 0) sb.AppendLine("No events were recorded.");
        else
        {
            sb.AppendLine("| Time | What happened | Where | Details |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in events.Take(200))
                sb.AppendLine($"| {Clock(e.Timestamp)} | {Lexicon.EventLabel(e.EventType)} | {e.TargetName} | {e.Description.Replace("|", "/")} |");
            if (events.Count > 200) sb.AppendLine($"\n*{events.Count - 200} more events are in the session file.*");
        }
        sb.AppendLine();

        if (!string.IsNullOrEmpty(s.GameDisplayName))
        {
            sb.AppendLine("## The game");
            sb.AppendLine();
            if (verdict.GameTime is TimeSpan played) sb.AppendLine($"{s.GameDisplayName} ran for {Dur(played)} of this recording.");
            if (s.GameProcesses.Count == 0) sb.AppendLine("The game itself was not seen running while recording.");
            foreach (var p in s.GameProcesses.Take(10))
                sb.AppendLine($"- {p.Name} seen {Clock(p.FirstSeenUtc)}-{Clock(p.LastSeenUtc)}{(p.ExitedUtc.HasValue ? $", closed {Clock(p.ExitedUtc.Value)}" : "")}");
            int udp = s.Connections.Count(c => c.Protocol == "UDP");
            int web = s.Connections.Count(c => c.Protocol == "TCP" && c.Remote != null && c.Scope == "Public");
            sb.AppendLine();
            sb.AppendLine(s.GameProcesses.Count == 0 ? ""
                : s.Connections.Count == 0 ? "The game made no network connections NetPulse could see: it was probably played offline."
                : $"The game had {udp} UDP socket{(udp == 1 ? "" : "s")} (where games send the match itself; Windows does not show where they go) and {web} connection{(web == 1 ? "" : "s")} to internet services (logins, chat, updates).");
            sb.AppendLine();
        }

        sb.AppendLine("## Limitations");
        sb.AppendLine();
        sb.AppendLine("- NetPulse measures your side of the connection: this PC, your home network, your provider and the internet, using your router and reference servers. It cannot measure a game's own server: games send the match over UDP, and Windows does not show programs without administrator rights where that traffic goes.");
        sb.AppendLine("- A moment that looked normal on your side can still have been lag between your provider and the game's server, or on the server itself. The game's own ping or network display shows that part.");
        sb.AppendLine("- This PC's traffic is the adapter's total: other devices at home are not included.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated by NetPulse on this PC. The session data stays local; nothing was uploaded.*");
        return sb.ToString();
    }

    /// <summary>One row per probe: time (UTC and local), target, method, success, latency, error.</summary>
    public static string BuildSamplesCsv(MonitoringSession s)
    {
        var names = s.Targets.ToDictionary(t => t.Id, t => t);
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUtc,TimestampLocal,Target,Method,Kind,Success,RttMs,Error");
        foreach (var x in s.Samples.OrderBy(x => x.T))
        {
            names.TryGetValue(x.TargetId, out var t);
            // every text field quoted, and nothing a spreadsheet would run as a formula
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:O},{1},{2},{3},{4},{5},{6},{7}",
                x.T, Local(x.T), UntrustedText.CsvField(t?.Name ?? x.TargetId), UntrustedText.CsvField(t == null ? "" : SessionAnalysis.MethodLabel(t)),
                t?.Kind.ToString() ?? "", x.Ok, x.RttMs?.ToString("F2", CultureInfo.InvariantCulture) ?? "", UntrustedText.CsvField(x.Error)));
        }
        return sb.ToString();
    }
}
