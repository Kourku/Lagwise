using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NetPulse.Core.Games;

namespace NetPulse.Core.Sessions;

/// <summary>
/// Builds the written report for a recorded session. Everything is derived from the local session file; nothing is sent anywhere.
/// The wording keeps measurements, possible explanations and confidence apart, and never presents a reference measurement as game-server ping.
/// </summary>
public static class SessionReportBuilder
{
    private static string Ms(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) + " ms" : "-";
    private static string Local(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss");
    private static string Clock(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("HH:mm:ss");
    private static string Dur(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{d.Seconds} s";

    public static string BuildMarkdown(MonitoringSession s, SessionStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# NetPulse session report: {s.Name}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(s.GameDisplayName))
            sb.AppendLine($"- **Game:** {s.GameDisplayName} (profile status: {s.GameSupportStatus ?? "unknown"})");
        sb.AppendLine($"- **Recorded:** {Local(s.StartedUtc)} to {(s.EndedUtc.HasValue ? Local(s.EndedUtc.Value) : "(recording was interrupted)")} (local time)");
        sb.AppendLine($"- **Length:** {Dur(stats.Duration)}{(s.IsComplete ? "" : " (incomplete: the recording ended without a normal stop, so data up to the last checkpoint is shown)")}");
        sb.AppendLine($"- **Connection:** {s.ActiveInterface ?? "unknown"}{(string.IsNullOrEmpty(s.InterfaceType) ? "" : $" ({s.InterfaceType})")}");
        sb.AppendLine($"- **Probes recorded:** {stats.ProbeCount:N0}{(s.SamplesTruncated ? " (sample cap reached; later samples were not stored)" : "")}");
        sb.AppendLine($"- **Events:** {s.Events.Count}, **lag markers:** {s.LagMarkers.Count}, **monitoring gaps:** {s.Gaps.Count}, **connection losses:** {s.Disconnects.Count}");
        sb.AppendLine();
        sb.AppendLine("> These are measurements of your connection (your router and reference servers). They are **not** the ping shown inside a game, and a reference server is not the game's server.");
        sb.AppendLine();

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

        sb.AppendLine("## Lag markers");
        sb.AppendLine();
        if (stats.LagCorrelations.Count == 0)
        {
            sb.AppendLine("No lag markers were added. Pressing **Mark lag** when you notice lag lets this report check what the measurements showed at that moment.");
        }
        foreach (var c in stats.LagCorrelations)
        {
            sb.AppendLine($"### {Clock(c.MarkerUtc)}{(string.IsNullOrEmpty(c.Note) ? "" : $" - {c.Note}")}");
            sb.AppendLine();
            var affected = c.Affected.ToList();
            if (affected.Count == 0 && c.NearbyEvents.Count == 0)
                sb.AppendLine($"Nothing unusual was measured within {SessionAnalysis.LagWindowSeconds} s of this moment. The lag may have been in the game's own servers or a path these targets do not share.");
            foreach (var a in affected)
                sb.AppendLine($"- {a.Name}: median {Ms(a.WindowMedianMs)} around this moment versus {Ms(a.SessionMedianMs)} for the session; {a.WindowFailures} of {a.WindowProbes} probes unanswered.");
            foreach (var e in c.NearbyEvents.Take(8))
                sb.AppendLine($"- {Clock(e.Timestamp)} {Lexicon.EventLabel(e.EventType)}: {e.Description}");
            sb.AppendLine();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (s.Findings.Count == 0)
        {
            sb.AppendLine("No diagnostic findings were stored with this session.");
            sb.AppendLine();
        }
        foreach (var f in s.Findings)
        {
            sb.AppendLine($"### {f.Title} (confidence: {f.Confidence})");
            sb.AppendLine();
            foreach (var o in f.Observations) sb.AppendLine($"- **Observed:** {o}");
            foreach (var x in f.PossibleExplanations) sb.AppendLine($"- **Possible explanation:** {x}");
            foreach (var x in f.Evidence) sb.AppendLine($"- **Evidence:** {x}");
            foreach (var x in f.Contradictions) sb.AppendLine($"- **Points the other way:** {x}");
            if (!string.IsNullOrWhiteSpace(f.NextTest)) sb.AppendLine($"- **Next test:** {f.NextTest}");
            sb.AppendLine();
        }

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
            sb.AppendLine("## Game processes and connections");
            sb.AppendLine();
            if (s.GameProcesses.Count == 0) sb.AppendLine("The game process was not detected while recording.");
            foreach (var p in s.GameProcesses)
                sb.AppendLine($"- {p.Name} (PID {p.Pid}) seen {Clock(p.FirstSeenUtc)}-{Clock(p.LastSeenUtc)}{(p.ExitedUtc.HasValue ? $", exited {Clock(p.ExitedUtc.Value)}" : "")}");
            sb.AppendLine();
            if (s.Connections.Count > 0)
            {
                sb.AppendLine($"{s.Connections.Count} sockets were observed on the game process. Their role is **unknown**: a game also contacts login, matchmaking, telemetry, voice and download services, so none is assumed to be the match server.");
                sb.AppendLine();
                sb.AppendLine("| Protocol | Remote endpoint | Where | First seen | Last seen |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var c in s.Connections.OrderBy(c => c.FirstSeenUtc).Take(60))
                    sb.AppendLine($"| {c.Protocol} | {c.Remote ?? "(remote peer not visible)"} | {c.Scope} | {Clock(c.FirstSeenUtc)} | {Clock(c.LastSeenUtc)} |");
                if (s.Connections.Count > 60) sb.AppendLine($"\n*{s.Connections.Count - 60} more are in the session file.*");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Route information");
        sb.AppendLine();
        sb.AppendLine("No route trace is stored with a session. Use the Route trace screen to follow the path to an address; a hop that looks slow or lossy only matters if the hops after it are affected too.");
        sb.AppendLine();

        sb.AppendLine("## Limitations");
        sb.AppendLine();
        if (s.Limitations.Count == 0) sb.AppendLine("- Reference measurements describe your connection in general, not a game server.");
        foreach (var l in s.Limitations) sb.AppendLine($"- {l}");
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
            string name = (t?.Name ?? x.TargetId).Replace("\"", "\"\"");
            string error = (x.Error ?? "").Replace("\"", "\"\"");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:O},{1},\"{2}\",{3},{4},{5},{6},\"{7}\"",
                x.T, Local(x.T), name, t == null ? "" : SessionAnalysis.MethodLabel(t), t?.Kind.ToString() ?? "", x.Ok, x.RttMs?.ToString("F2", CultureInfo.InvariantCulture) ?? "", error));
        }
        return sb.ToString();
    }
}
