using System;
using System.Collections.Generic;
using System.Linq;
using Lagwise.Core.Models;
using Lagwise.Core.Statistics;

namespace Lagwise.Core.Sessions;

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
    IReadOnlyList<LagTargetView> Targets,
    LagTraffic? Traffic = null)
{
    /// <summary>Targets that looked clearly worse than their own session norm around the marker.</summary>
    public IEnumerable<LagTargetView> Affected => Targets.Where(t => t.LookedWorse);
}

/// <summary>
/// This PC's own download and upload around a lag marker (recordings from 0.13 on). Busy means at least
/// <see cref="SessionAnalysis.BusyMinSeconds"/> seconds at the heavy-traffic limit or above.
/// </summary>
public sealed record LagTraffic(double PeakRxBps, double PeakTxBps, int BusySeconds, int Seconds)
{
    public bool Busy => BusySeconds >= SessionAnalysis.BusyMinSeconds;

    /// <summary>One sentence for the History page and the report.</summary>
    public string Describe()
    {
        string peaks = $"up to {Services.AdapterActivityMonitor.FormatBps(PeakRxBps)} down and {Services.AdapterActivityMonitor.FormatBps(PeakTxBps)} up";
        return Busy
            ? $"This PC was busy: {peaks} around this moment ({BusySeconds} s at the heavy-traffic limit or above). That traffic competes with the game."
            : $"This PC's own traffic was light ({peaks}), so a download on this PC was not the cause. Other devices at home are not included.";
    }
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
    /// <summary>Lag markers with traffic data, and how many of them came while this PC was busy.</summary>
    public int LagWithTraffic => LagCorrelations.Count(c => c.Traffic != null);
    public int LagWhileBusy => LagCorrelations.Count(c => c.Traffic?.Busy == true);

    /// <summary>One line about lag and this PC's own traffic; empty when the recording has no traffic data or no markers.</summary>
    public string LagTrafficSummary => LagWithTraffic == 0 ? ""
        : LagWhileBusy == 0
            ? (LagWithTraffic == 1 ? "This PC's own traffic was light when you marked lag, so a download on this PC was not the cause."
                : $"This PC's own traffic was light at all {LagWithTraffic} moments you marked, so downloads on this PC were not the cause.")
            : $"{LagWhileBusy} of the {LagWithTraffic} moment{(LagWithTraffic == 1 ? "" : "s")} you marked came while this PC was busy downloading or uploading. That traffic competes with the game; pausing it is the first thing to try.";
}

/// <summary>Derives the numbers a person wants from a recorded session: per-target statistics and what happened around each lag marker.</summary>
public static class SessionAnalysis
{
    /// <summary>Seconds either side of a lag marker that count as "around" it.</summary>
    public const int LagWindowSeconds = 15;
    public const int LagEventWindowSeconds = 30;
    /// <summary>The default heavy-traffic limit (Settings > Events), used when none is given.</summary>
    public const double DefaultBusyBps = 15_000_000;
    public const int BusyMinSeconds = 2;

    /// <param name="busyBps">Traffic counted as "busy" around lag markers: the heavy-traffic limit from Settings.</param>
    public static SessionStats Compute(MonitoringSession session, double busyBps = DefaultBusyBps)
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
            LagCorrelations = session.LagMarkers.OrderBy(m => m.T).Select(m => Correlate(session, byTarget, m, busyBps)).ToList(),
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

    private static LagCorrelation Correlate(MonitoringSession session, Dictionary<string, List<SessionSample>> byTarget, LagMarker marker, double busyBps)
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

        LagTraffic? traffic = null;
        var seconds = session.Throughput.Where(p => p.T >= from && p.T <= to).ToList();
        if (seconds.Count > 0)
            traffic = new LagTraffic(seconds.Max(p => (double)p.Rx), seconds.Max(p => (double)p.Tx),
                seconds.Count(p => Math.Max(p.Rx, p.Tx) >= busyBps), seconds.Count);

        return new LagCorrelation(marker.T, marker.Note, events, views, traffic);
    }
}
