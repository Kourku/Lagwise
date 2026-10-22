using System;
using System.Collections.Generic;
using System.Linq;

namespace Lagwise.Core.Sessions;

/// <summary>One target measured in both recordings.</summary>
public sealed record TargetComparison(
    string TargetId,
    string Name,
    string KindLabel,
    double? MedianA, double? MedianB,
    double? P95A, double? P95B,
    double? VariationA, double? VariationB,
    double LossA, double LossB)
{
    public double? MedianDelta => MedianA.HasValue && MedianB.HasValue ? MedianB - MedianA : null;
    public double? VariationDelta => VariationA.HasValue && VariationB.HasValue ? VariationB - VariationA : null;
    public double LossDelta => LossB - LossA;
}

public sealed record SessionComparisonResult(IReadOnlyList<TargetComparison> Targets, IReadOnlyList<string> OnlyInA, IReadOnlyList<string> OnlyInB, string Summary);

/// <summary>
/// Compares two recordings target by target ("A" is the reference, "B" the one being judged), for questions such as
/// "is cable better than Wi-Fi?" or "is it worse in the evening?". Only targets present in both are compared,
/// and differences smaller than normal noise are called "about the same".
/// </summary>
public static class SessionComparison
{
    /// <summary>Changes below these are reported as "about the same".</summary>
    public const double MedianNoiseMs = 3, VariationNoiseMs = 2, LossNoisePercent = 0.5;

    public static SessionComparisonResult Compare(SessionStats a, SessionStats b)
    {
        var bById = b.Targets.ToDictionary(t => t.TargetId);
        var rows = new List<TargetComparison>();
        foreach (var ta in a.Targets)
        {
            if (!bById.TryGetValue(ta.TargetId, out var tb)) continue;
            rows.Add(new TargetComparison(ta.TargetId, ta.Name, ta.KindLabel,
                ta.MedianMs, tb.MedianMs, ta.P95Ms, tb.P95Ms, ta.VariationMs, tb.VariationMs, ta.LossPercent, tb.LossPercent));
        }

        var onlyA = a.Targets.Where(t => !bById.ContainsKey(t.TargetId)).Select(t => t.Name).ToList();
        var aIds = a.Targets.Select(t => t.TargetId).ToHashSet();
        var onlyB = b.Targets.Where(t => !aIds.Contains(t.TargetId)).Select(t => t.Name).ToList();

        return new SessionComparisonResult(rows, onlyA, onlyB, Summarize(rows));
    }

    private static string Summarize(IReadOnlyList<TargetComparison> rows)
    {
        if (rows.Count == 0) return "The two recordings have no measurement target in common, so they cannot be compared.";

        var parts = new List<string>();
        var medians = rows.Where(r => r.MedianDelta.HasValue).Select(r => r.MedianDelta!.Value).ToList();
        var variations = rows.Where(r => r.VariationDelta.HasValue).Select(r => r.VariationDelta!.Value).ToList();

        if (medians.Count > 0) parts.Add(Describe("typical ping", Statistics.StatisticalCalculator.CalculateMedian(medians), MedianNoiseMs, "ms"));
        if (variations.Count > 0) parts.Add(Describe("ping stability", -Statistics.StatisticalCalculator.CalculateMedian(variations), VariationNoiseMs, "ms", invertedWording: true));
        double loss = rows.Average(r => r.LossDelta);
        parts.Add(Math.Abs(loss) < LossNoisePercent ? "probe loss about the same"
            : loss > 0 ? $"probe loss higher by {loss:F1} points" : $"probe loss lower by {-loss:F1} points");

        return "Compared with the first recording, the second had " + string.Join(", ", parts) + " (median across the targets both measured).";
    }

    private static string Describe(string what, double delta, double noise, string unit, bool invertedWording = false)
    {
        if (Math.Abs(delta) < noise) return $"{what} about the same";
        if (invertedWording) return delta > 0 ? $"steadier ping ({delta:F1} {unit} less variation)" : $"less steady ping ({-delta:F1} {unit} more variation)";
        return delta > 0 ? $"{what} higher by {delta:F1} {unit}" : $"{what} lower by {-delta:F1} {unit}";
    }
}
