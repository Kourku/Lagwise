using System;
using System.Collections.Generic;
using System.Linq;
using Lagwise.Core.Statistics;

namespace Lagwise.Core.Sessions;

/// <summary>How the connection behaved during one hour of the day (local time), pooled over every recording.</summary>
public sealed class HourPattern
{
    public int Hour { get; init; }
    /// <summary>Reference probes recorded in this hour (replies and failures).</summary>
    public int SampleCount { get; init; }
    /// <summary>Recordings that contributed samples to this hour.</summary>
    public int RecordingCount { get; init; }
    /// <summary>
    /// Typical ping in this hour minus each server's own typical ping over all recordings, averaged over servers
    /// (weighted by samples). Positive means slower than usual. Comparing every server with itself keeps a far-away
    /// server from making an hour look bad just because it was measured more often then.
    /// </summary>
    public double? DeltaMs { get; init; }
    /// <summary>Probes without a reply, in percent.</summary>
    public double LossPercent { get; init; }
    public int LagMarkers { get; init; }
    public bool HasEnoughData { get; init; }
}

public sealed class PatternResult
{
    public IReadOnlyList<HourPattern> Hours { get; init; } = Array.Empty<HourPattern>();
    public int RecordingCount { get; init; }
    public int SampleCount { get; init; }
    public HourPattern? WorstHour { get; init; }
    public HourPattern? BestHour { get; init; }
    /// <summary>A plain-language conclusion, careful to say when there is too little data.</summary>
    public string Summary { get; init; } = string.Empty;
    public int HoursWithData => Hours.Count(h => h.HasEnoughData);
}

/// <summary>
/// "When is my connection worst?": pools the reference-server probes of all recordings by local hour of day.
/// Only reference endpoints count (the router and DNS timings measure other things, and observed game endpoints may
/// filter probes). An hour needs <see cref="MinSamplesPerHour"/> probes before it is rated at all.
/// </summary>
public sealed class ConnectionPatterns
{
    public const int MinSamplesPerHour = 120;
    /// <summary>Differences smaller than this are treated as noise when naming a worst hour.</summary>
    public const double NoticeableDeltaMs = 4.0;
    public const double NoticeableLossPercent = 1.0;

    private readonly Func<DateTime, DateTime> _toLocal;
    // per server: replies per hour; plus per-hour counters
    private readonly Dictionary<string, List<double>[]> _rtts = new();
    private readonly int[] _samples = new int[24];
    private readonly int[] _failures = new int[24];
    private readonly int[] _markers = new int[24];
    private readonly HashSet<string>[] _recordings = Enumerable.Range(0, 24).Select(_ => new HashSet<string>()).ToArray();
    private int _recordingCount;

    public ConnectionPatterns(Func<DateTime, DateTime>? toLocal = null)
    {
        _toLocal = toLocal ?? (t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime());
    }

    /// <summary>Adds one recording. Recordings can be added one at a time, so they never all need to be in memory.</summary>
    public void Add(MonitoringSession session)
    {
        var reference = session.Targets.Where(t => t.Kind == MeasurementKind.ReferenceEndpoint)
            .ToDictionary(t => t.Id, t => $"{t.Protocol}|{t.Host}", StringComparer.Ordinal);
        if (reference.Count == 0) return;

        bool any = false;
        foreach (var s in session.Samples)
        {
            if (!reference.TryGetValue(s.TargetId, out var key)) continue;
            int h = _toLocal(s.T).Hour;
            _samples[h]++;
            _recordings[h].Add(session.Id);
            any = true;
            if (s.Ok && s.RttMs is double rtt)
            {
                if (!_rtts.TryGetValue(key, out var byHour)) _rtts[key] = byHour = Enumerable.Range(0, 24).Select(_ => new List<double>()).ToArray();
                byHour[h].Add(rtt);
            }
            else _failures[h]++;
        }
        if (!any) return;
        _recordingCount++;
        foreach (var m in session.LagMarkers) _markers[_toLocal(m.T).Hour]++;
    }

    public PatternResult Build()
    {
        // each server's usual ping over everything recorded
        var usual = _rtts.ToDictionary(kv => kv.Key, kv => StatisticalCalculator.CalculateMedian(kv.Value.SelectMany(l => l).ToList()));

        var hours = new List<HourPattern>(24);
        for (int h = 0; h < 24; h++)
        {
            double weighted = 0; int weight = 0;
            foreach (var (key, byHour) in _rtts)
            {
                var list = byHour[h];
                if (list.Count == 0) continue;
                weighted += (StatisticalCalculator.CalculateMedian(list) - usual[key]) * list.Count;
                weight += list.Count;
            }
            hours.Add(new HourPattern
            {
                Hour = h,
                SampleCount = _samples[h],
                RecordingCount = _recordings[h].Count,
                DeltaMs = weight > 0 ? weighted / weight : null,
                LossPercent = _samples[h] > 0 ? 100.0 * _failures[h] / _samples[h] : 0,
                LagMarkers = _markers[h],
                HasEnoughData = _samples[h] >= MinSamplesPerHour && weight > 0
            });
        }

        var rated = hours.Where(h => h.HasEnoughData).ToList();
        HourPattern? worst = null, best = null;
        if (rated.Count >= 2)
        {
            worst = rated.OrderByDescending(Badness).First();
            best = rated.OrderBy(Badness).First();
            if (worst.DeltaMs < NoticeableDeltaMs && worst.LossPercent < NoticeableLossPercent) worst = null;
        }

        return new PatternResult
        {
            Hours = hours,
            RecordingCount = _recordingCount,
            SampleCount = _samples.Sum(),
            WorstHour = worst,
            BestHour = best,
            Summary = Summarize(rated, worst, best)
        };
    }

    /// <summary>Ranks hours: 1% probe loss weighs like 10 ms of extra ping.</summary>
    private static double Badness(HourPattern h) => (h.DeltaMs ?? 0) + 10 * h.LossPercent;

    public static string HourRange(int hour) => $"{hour:00}:00–{(hour + 1) % 24:00}:00";

    private string Summarize(List<HourPattern> rated, HourPattern? worst, HourPattern? best)
    {
        if (_recordingCount == 0) return "No recordings with reference measurements yet. Record a few gaming sessions at different times of day, then look again.";
        if (rated.Count < 2)
            return $"Not enough data yet: an hour needs about {MinSamplesPerHour / 60} minutes of recording before it is rated, and at least two hours are needed to compare. " +
                   $"So far {rated.Count} hour{(rated.Count == 1 ? " has" : "s have")} enough.";
        if (worst == null)
            return $"No hour stands out. Across the {rated.Count} hours with enough data, ping stays within {NoticeableDeltaMs:0} ms of usual and probe loss under {NoticeableLossPercent:0}%.";

        var parts = new List<string>();
        if (worst.DeltaMs is double d && d >= NoticeableDeltaMs) parts.Add($"ping about {d:0} ms above your usual");
        if (worst.LossPercent >= NoticeableLossPercent) parts.Add($"{worst.LossPercent:0.#}% probe loss");
        string text = $"Your connection is worst around {HourRange(worst.Hour)}: {string.Join(" and ", parts)}";
        if (best != null && best.Hour != worst.Hour) text += $". It is best around {HourRange(best.Hour)}";
        text += $". Based on {rated.Count} hours with enough data from {_recordingCount} recording{(_recordingCount == 1 ? "" : "s")}.";
        if (worst.RecordingCount < 3) text += " Only a few recordings cover that hour, so it may be a one-off rather than a pattern.";
        else if (worst.Hour is >= 17 and <= 23) text += " A slowdown every evening often means your provider's network is congested at peak time; worth showing them.";
        else text += " A slowdown that repeats at the same hour is worth checking: something on your network (backups, updates, other people streaming) or your provider.";
        return text;
    }
}
