using System;
using System.Collections.Generic;
using System.Linq;
using Lagwise.Core.Statistics;

namespace Lagwise.Core.Sessions;

public enum ChartMode
{
    /// <summary>Slowest reply in each time slice (so short spikes stay visible).</summary>
    Latency,
    /// <summary>Average difference between consecutive replies in each time slice.</summary>
    Variation,
    /// <summary>Share of probes with no reply in each time slice.</summary>
    Loss
}

public sealed record ChartPoint(double X, double Y);

public sealed class ChartSeries
{
    public required string TargetId { get; init; }
    public required string Name { get; init; }
    public MeasurementKind Kind { get; init; }
    public List<ChartPoint> Points { get; init; } = new();
}

public sealed class ChartData
{
    public ChartMode Mode { get; init; }
    public DateTime StartUtc { get; init; }
    public double DurationSeconds { get; init; }
    public double BucketSeconds { get; init; }
    /// <summary>Top of the Y axis. Rare extreme values are clipped to it so one huge spike does not flatten everything else.</summary>
    public double YCap { get; init; }
    public string Unit => Mode == ChartMode.Loss ? "%" : "ms";
    public IReadOnlyList<ChartSeries> Series { get; init; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<double> LagMarkerSeconds { get; init; } = Array.Empty<double>();
    public IReadOnlyList<(double Start, double End)> GapSeconds { get; init; } = Array.Empty<(double, double)>();
    public IReadOnlyList<(double Start, double End)> OutageSeconds { get; init; } = Array.Empty<(double, double)>();
}

/// <summary>Turns a recorded session into plot-ready series: downsampled to a fixed number of time slices, independent of the UI.</summary>
public static class SessionChartBuilder
{
    /// <summary>
    /// Chart of the last <paramref name="window"/> of live measurements (the engine's in-memory history), for the Overview.
    /// Uses the same rules as a recording's chart so the two always look and read the same.
    /// </summary>
    public static ChartData BuildLive(IEnumerable<Models.Measurement> history, IEnumerable<SessionTarget> targets, DateTime nowUtc, TimeSpan window, ChartMode mode, int maxBuckets = 180)
    {
        var wanted = targets.ToList();
        var ids = wanted.Select(t => t.Id).ToHashSet();
        var start = nowUtc - window;
        var samples = history
            .Where(m => m.Timestamp >= start && m.Timestamp <= nowUtc && ids.Contains(m.TargetId))
            .Select(m => new SessionSample { T = m.Timestamp, TargetId = m.TargetId, Ok = m.Success, RttMs = m.Success ? m.RttMs : null })
            .ToList();

        // just after start-up there is less history than the window: show what exists (at least 30 s) instead of an empty graph
        if (samples.Count > 0)
        {
            var first = samples.Min(x => x.T);
            if (first > start) start = first < nowUtc.AddSeconds(-30) ? first : nowUtc.AddSeconds(-30);
        }

        var session = new MonitoringSession { StartedUtc = start, EndedUtc = nowUtc, Targets = wanted, Samples = samples };
        return Build(session, mode, null, maxBuckets);
    }

    public static ChartData Build(MonitoringSession s, ChartMode mode, ISet<string>? targetIds = null, int maxBuckets = 400)
    {
        var end = s.EndedUtc ?? (s.Samples.Count > 0 ? s.Samples.Max(x => x.T) : s.StartedUtc);
        double duration = Math.Max(1, (end - s.StartedUtc).TotalSeconds);
        double bucket = Math.Max(1, Math.Ceiling(duration / Math.Max(20, maxBuckets)));

        var byTarget = s.Samples.GroupBy(x => x.TargetId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.T).ToList());
        var series = new List<ChartSeries>();

        foreach (var t in s.Targets)
        {
            if (targetIds != null && !targetIds.Contains(t.Id)) continue;
            if (!byTarget.TryGetValue(t.Id, out var samples)) continue;

            var cs = new ChartSeries { TargetId = t.Id, Name = t.Name, Kind = t.Kind };
            foreach (var group in samples.GroupBy(x => (long)Math.Floor((x.T - s.StartedUtc).TotalSeconds / bucket)).OrderBy(g => g.Key))
            {
                double x = (group.Key + 0.5) * bucket;
                var ok = group.Where(v => v.Ok && v.RttMs.HasValue).Select(v => v.RttMs!.Value).ToList();

                switch (mode)
                {
                    case ChartMode.Latency when ok.Count > 0:
                        cs.Points.Add(new ChartPoint(x, ok.Max()));
                        break;
                    case ChartMode.Variation when ok.Count >= 2:
                        double sum = 0;
                        for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
                        cs.Points.Add(new ChartPoint(x, sum / (ok.Count - 1)));
                        break;
                    case ChartMode.Loss:
                        cs.Points.Add(new ChartPoint(x, StatisticalCalculator.CalculatePacketLoss(group.Count() - ok.Count, group.Count())));
                        break;
                }
            }
            if (cs.Points.Count > 0) series.Add(cs);
        }

        double cap;
        if (mode == ChartMode.Loss)
        {
            cap = 100;
        }
        else
        {
            var all = series.SelectMany(x => x.Points.Select(p => p.Y)).ToList();
            double floor = mode == ChartMode.Latency ? 20 : 5;
            cap = all.Count == 0 ? floor : Math.Max(floor, StatisticalCalculator.CalculatePercentile(all, 0.99) * 1.15);
        }

        double Sec(DateTime t) => Math.Clamp((t - s.StartedUtc).TotalSeconds, 0, duration);

        return new ChartData
        {
            Mode = mode,
            StartUtc = s.StartedUtc,
            DurationSeconds = duration,
            BucketSeconds = bucket,
            YCap = cap,
            Series = series,
            LagMarkerSeconds = s.LagMarkers.Select(m => Sec(m.T)).ToList(),
            GapSeconds = s.Gaps.Select(g => (Sec(g.StartUtc), Sec(g.EndUtc))).ToList(),
            OutageSeconds = s.Disconnects.Select(d => (Sec(d.StartUtc), Sec(d.EndUtc ?? end))).ToList()
        };
    }
}
