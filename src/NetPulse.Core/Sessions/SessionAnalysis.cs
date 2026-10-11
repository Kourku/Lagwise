using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Sessions;

public sealed record TargetStats(
    string TargetId,
    string Name,
    MeasurementKind Kind,
    string Method,
    int Probes,
    int Replies,
    double LossPercent,
    double? MinMs,
    double? MedianMs,
    double? P95Ms,
    double? MaxMs,
    /// <summary>Mean absolute difference between consecutive replies (latency variation).</summary>
    double? VariationMs,
    int LongestFailureStreak)
{
    public string KindLabel => Kind switch
    {
        MeasurementKind.LocalGateway => "Your router",
        MeasurementKind.DnsResolution => "DNS lookup",
        MeasurementKind.ReferenceEndpoint => "Reference server (not a game server)",
        MeasurementKind.GameProcessEndpoint => "Endpoint seen on the game (role unknown)",
        _ => "Game server"
    };
}

/// <summary>How the measurements looked in the seconds around a moment the user marked as laggy.</summary>
public sealed record LagCorrelation(
    DateTime MarkerUtc,
    string? Note,
    IReadOnlyList<ConnectionEvent> NearbyEvents,
    IReadOnlyList<LagTargetView> Targets)
{
    /// <summary>Targets that looked clearly worse than their own session norm around the marker.</summary>
    public IEnumerable<LagTargetView> Affected => Targets.Where(t => t.LookedWorse);
}

public sealed record LagTargetView(string Name, double? WindowMedianMs, double? SessionMedianMs, int WindowProbes, int WindowFailures, bool LookedWorse);

public sealed class SessionStats
{
    public TimeSpan Duration { get; init; }
    public int ProbeCount { get; init; }
    public IReadOnlyList<TargetStats> Targets { get; init; } = Array.Empty<TargetStats>();
    public IReadOnlyDictionary<string, int> EventCounts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<LagCorrelation> LagCorrelations { get; init; } = Array.Empty<LagCorrelation>();
    public double GapSeconds { get; init; }
    public double DisconnectSeconds { get; init; }
}

/// <summary>Derives the numbers a person wants from a recorded session: per-target statistics and what happened around each lag marker.</summary>
public static class SessionAnalysis
{
    /// <summary>Seconds either side of a lag marker that count as "around" it.</summary>
    public const int LagWindowSeconds = 15;
    public const int LagEventWindowSeconds = 30;

    public static SessionStats Compute(MonitoringSession session)
    {
        var end = session.EndedUtc ?? (session.Samples.Count > 0 ? session.Samples.Max(s => s.T) : session.StartedUtc);
        var byTarget = session.Samples.GroupBy(s => s.TargetId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.T).ToList());

        var targets = new List<TargetStats>();
        foreach (var t in session.Targets)
        {
            byTarget.TryGetValue(t.Id, out var samples);
            targets.Add(StatsFor(t, samples ?? new List<SessionSample>()));
        }

        return new SessionStats
        {
            Duration = end > session.StartedUtc ? end - session.StartedUtc : TimeSpan.Zero,
            ProbeCount = session.Samples.Count,
            Targets = targets,
            EventCounts = session.Events.GroupBy(e => e.EventType).ToDictionary(g => g.Key, g => g.Count()),
            LagCorrelations = session.LagMarkers.OrderBy(m => m.T).Select(m => Correlate(session, byTarget, m)).ToList(),
            GapSeconds = session.Gaps.Sum(g => g.DurationSeconds),
            DisconnectSeconds = session.Disconnects.Sum(d => (d.EndUtc ?? end) > d.StartUtc ? ((d.EndUtc ?? end) - d.StartUtc).TotalSeconds : 0)
        };
    }

    private static TargetStats StatsFor(SessionTarget t, List<SessionSample> samples)
    {
        var ok = samples.Where(s => s.Ok && s.RttMs.HasValue).Select(s => s.RttMs!.Value).ToList();
        int failures = samples.Count - ok.Count;

        int longest = 0, run = 0;
        foreach (var s in samples)
        {
            run = s.Ok ? 0 : run + 1;
            longest = Math.Max(longest, run);
        }

        double? variation = null;
        if (ok.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            variation = sum / (ok.Count - 1);
        }

        return new TargetStats(
            t.Id, t.Name, t.Kind, MethodLabel(t), samples.Count, ok.Count,
            StatisticalCalculator.CalculatePacketLoss(failures, samples.Count),
            ok.Count > 0 ? ok.Min() : null,
            ok.Count > 0 ? StatisticalCalculator.CalculateMedian(ok) : null,
            ok.Count > 0 ? StatisticalCalculator.CalculatePercentile(ok, 0.95) : null,
            ok.Count > 0 ? ok.Max() : null,
            variation,
            longest);
    }

    public static string MethodLabel(SessionTarget t) => t.Protocol switch
    {
        NetworkProtocol.Icmp => "ICMP ping",
        NetworkProtocol.Tcp => "TCP connect",
        NetworkProtocol.Https => "HTTPS handshake",
        NetworkProtocol.Dns => "DNS lookup",
        _ => t.Protocol.ToString()
    };

    private static LagCorrelation Correlate(MonitoringSession session, Dictionary<string, List<SessionSample>> byTarget, LagMarker marker)
    {
        var from = marker.T.AddSeconds(-LagWindowSeconds);
        var to = marker.T.AddSeconds(LagWindowSeconds);

        var events = session.Events
            .Where(e => e.EventType != "LagMarker" && Math.Abs((e.Timestamp - marker.T).TotalSeconds) <= LagEventWindowSeconds)
            .OrderBy(e => e.Timestamp)
            .ToList();

        var views = new List<LagTargetView>();
        foreach (var t in session.Targets)
        {
            if (!byTarget.TryGetValue(t.Id, out var all)) continue;
            var window = all.Where(s => s.T >= from && s.T <= to).ToList();
            if (window.Count == 0) continue;

            var windowOk = window.Where(s => s.Ok && s.RttMs.HasValue).Select(s => s.RttMs!.Value).ToList();
            var sessionOk = all.Where(s => s.Ok && s.RttMs.HasValue).Select(s => s.RttMs!.Value).ToList();
            double? windowMedian = windowOk.Count > 0 ? StatisticalCalculator.CalculateMedian(windowOk) : null;
            double? sessionMedian = sessionOk.Count > 0 ? StatisticalCalculator.CalculateMedian(sessionOk) : null;
            int failures = window.Count - windowOk.Count;

            // "worse" is judged against this target's own norm: at least double and 30 ms more, or a clear burst of failures
            bool slower = windowMedian.HasValue && sessionMedian.HasValue && windowMedian > sessionMedian * 2 && windowMedian - sessionMedian > 30;
            bool lossy = window.Count >= 5 && failures * 100.0 / window.Count >= 20;
            views.Add(new LagTargetView(t.Name, windowMedian, sessionMedian, window.Count, failures, slower || lossy));
        }

        return new LagCorrelation(marker.T, marker.Note, events, views);
    }
}
