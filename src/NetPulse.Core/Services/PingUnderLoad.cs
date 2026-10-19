using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

/// <summary>Ping during a stretch of heavy traffic on this PC, compared with the minute before it.</summary>
public sealed record LoadPing(double RestMs, double LoadedMs, double RiseMs, double LossPercent, int Servers);

/// <summary>
/// Passive bufferbloat: when this PC itself moves a lot of data (a game update, a cloud backup), the plain-ping reference
/// servers show whether that load made the connection queue. Each server is compared with its own ping just before the
/// traffic, so a far-away server never looks like a rise. This is real use, not a test: the line may not have been full,
/// so a small rise does not prove there is no bufferbloat.
/// </summary>
public static class PingUnderLoad
{
    /// <summary>How far back "before" reaches.</summary>
    public static readonly TimeSpan BeforeWindow = TimeSpan.FromSeconds(60);
    /// <summary>The first seconds of a transfer speed up gradually; they are not counted as "during".</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    /// <summary>Replies a server needs before and during the traffic to be compared.</summary>
    public const int MinReplies = 5;

    /// <summary>A rise up to this is steady (bufferbloat grade A), above <see cref="ClearRiseMs"/> clearly too much (C or worse).</summary>
    public const double SteadyRiseMs = 15;
    public const double ClearRiseMs = 30;

    /// <summary>
    /// Each reference server's typical ping in the minute before <paramref name="startUtc"/> (not before
    /// <paramref name="notBeforeUtc"/>, the end of an earlier stretch). Taken when the stretch is reported, because a long
    /// stretch outlasts the probe history.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Before(IEnumerable<Measurement> history, IReadOnlySet<string> referenceIds,
        DateTime startUtc, DateTime notBeforeUtc = default)
    {
        var from = startUtc - BeforeWindow;
        if (notBeforeUtc > from) from = notBeforeUtc;
        var to = startUtc.AddSeconds(-1);
        return Replies(history, referenceIds, from, to)
            .Where(g => g.Value.Count >= MinReplies)
            .ToDictionary(g => g.Key, g => StatisticalCalculator.CalculateMedian(g.Value));
    }

    /// <summary>Ping during the stretch against <paramref name="before"/>; null when no server has enough replies on both sides.</summary>
    public static LoadPing? During(IEnumerable<Measurement> history, IReadOnlySet<string> referenceIds,
        IReadOnlyDictionary<string, double> before, DateTime startUtc, DateTime endUtc)
    {
        if (before.Count == 0) return null;
        var from = startUtc + SettleTime;
        if (endUtc <= from) return null;

        var probes = Probes(history, referenceIds, from, endUtc).ToList();
        var during = probes.Where(m => m.Success).GroupBy(m => m.TargetId).ToDictionary(g => g.Key, g => g.Select(m => m.RttMs).ToList());

        var rest = new List<double>();
        var loaded = new List<double>();
        var rises = new List<double>();
        foreach (var (id, beforeMs) in before)
        {
            if (!during.TryGetValue(id, out var replies) || replies.Count < MinReplies) continue;
            double now = StatisticalCalculator.CalculateMedian(replies);
            rest.Add(beforeMs);
            loaded.Add(now);
            rises.Add(now - beforeMs);
        }
        if (rises.Count == 0) return null;

        var compared = probes.Where(m => before.ContainsKey(m.TargetId)).ToList();
        double loss = compared.Count == 0 ? 0 : compared.Count(m => !m.Success) * 100.0 / compared.Count;
        return new LoadPing(
            Math.Round(StatisticalCalculator.CalculateMedian(rest), 1),
            Math.Round(StatisticalCalculator.CalculateMedian(loaded), 1),
            Math.Round(StatisticalCalculator.CalculateMedian(rises), 1),
            Math.Round(loss, 1),
            rises.Count);
    }

    /// <summary>The timeline text for a stretch that has ended, with what it did to ping when that is known.</summary>
    public static string Describe(HeavyTrafficStretch h, LoadPing? ping)
    {
        string speeds = $"about {AdapterActivityMonitor.FormatBps(h.AverageRxBps)} down and {AdapterActivityMonitor.FormatBps(h.AverageTxBps)} up on average";
        string head = $"Heavy traffic on this PC ended after {Lexicon.Duration(h.Duration)} ({speeds}). ";
        if (ping == null)
            return head + "There were too few pings before or during it to tell whether it raised your ping.";

        string numbers = $"{ping.RestMs:0} ms before, {ping.LoadedMs:0} ms during";
        string loss = ping.LossPercent >= 5 ? $" {ping.LossPercent:0}% of the pings during it got no reply." : "";
        if (ping.RiseMs <= SteadyRiseMs)
            return head + $"Ping to the reference servers stayed steady meanwhile ({Signed(ping.RiseMs)}: {numbers}), so this load did not make your connection queue." + loss
                + " The line may not have been full; the Bufferbloat test measures under full load.";
        if (ping.RiseMs <= ClearRiseMs)
            return head + $"Ping to the reference servers rose a little meanwhile ({Signed(ping.RiseMs)}: {numbers}). Games can feel this as a slight delay." + loss;
        return head + $"Ping to the reference servers rose by {ping.RiseMs:0} ms meanwhile ({numbers}): the load made your connection queue (bufferbloat), which games feel as lag." + loss
            + " Pausing downloads while you play, or a router with Smart Queue Management (SQM), helps.";
    }

    private static string Signed(double ms) => ms >= 0 ? $"+{ms:0} ms" : $"{ms:0} ms";

    private static IEnumerable<Measurement> Probes(IEnumerable<Measurement> history, IReadOnlySet<string> referenceIds, DateTime from, DateTime to) =>
        history.Where(m => m.Protocol == NetworkProtocol.Icmp && referenceIds.Contains(m.TargetId) && m.Timestamp >= from && m.Timestamp <= to);

    private static Dictionary<string, List<double>> Replies(IEnumerable<Measurement> history, IReadOnlySet<string> referenceIds, DateTime from, DateTime to) =>
        Probes(history, referenceIds, from, to).Where(m => m.Success)
            .GroupBy(m => m.TargetId).ToDictionary(g => g.Key, g => g.Select(m => m.RttMs).ToList());
}
