using System;
using System.Collections.Generic;
using System.Linq;

namespace Lagwise.Core.Statistics;

/// <summary>
/// Time-based sliding window of probe outcomes. Samples older than the window duration are
/// discarded by <see cref="Prune"/>, so loss and variation describe *recent* conditions only.
/// Not thread-safe; callers synchronise externally. Time is always supplied by the caller
/// (never read from the clock) so behaviour is deterministic.
/// </summary>
public sealed class RollingWindow
{
    private readonly record struct Sample(DateTime Timestamp, bool Success, double RttMs);

    private readonly Queue<Sample> _samples = new();

    public TimeSpan Duration { get; }

    public RollingWindow(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
    }

    public void Add(DateTime timestamp, bool success, double rttMs)
    {
        _samples.Enqueue(new Sample(timestamp, success, success ? rttMs : 0));
        Prune(timestamp);
    }

    /// <summary>Removes samples that are older than <paramref name="now"/> minus the window duration.</summary>
    public void Prune(DateTime now)
    {
        var cutoff = now - Duration;
        while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
        {
            _samples.Dequeue();
        }
    }

    public int Count => _samples.Count;
    public int SuccessCount => _samples.Count(s => s.Success);
    public int FailureCount => _samples.Count(s => !s.Success);

    /// <summary>Percentage (0-100) of probes in the window that received no valid reply.</summary>
    public double LossPercent => StatisticalCalculator.CalculatePacketLoss(FailureCount, Count);

    private List<double> SuccessfulRtts() => _samples.Where(s => s.Success).Select(s => s.RttMs).ToList();

    public double MeanMs => StatisticalCalculator.CalculateMean(SuccessfulRtts());
    public double MedianMs => StatisticalCalculator.CalculateMedian(SuccessfulRtts());
    public double P95Ms => StatisticalCalculator.CalculatePercentile(SuccessfulRtts(), 0.95);

    /// <summary>
    /// Latency variation: mean absolute difference between consecutive successful replies in the window.
    /// 0 when fewer than two replies are available.
    /// </summary>
    public double JitterMs
    {
        get
        {
            var rtts = SuccessfulRtts();
            if (rtts.Count < 2) return 0;
            double sum = 0;
            for (int i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
            return sum / (rtts.Count - 1);
        }
    }
}
