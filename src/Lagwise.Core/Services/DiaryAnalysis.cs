using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lagwise.Core.Statistics;

namespace Lagwise.Core.Services;

public enum DiaryLevel
{
    /// <summary>Measured, but too briefly to rate.</summary>
    Thin,
    Good,
    Fair,
    Poor
}

/// <summary>What the diary is coloured by.</summary>
public enum DiaryMetric
{
    Overall,
    PingAboveUsual,
    Variation,
    ProbeLoss,
    Drops
}

/// <summary>One hour of the diary in local time, rated by the same rules of thumb as the "Ready to play?" checklist.</summary>
public sealed class DiaryCell
{
    /// <summary>An hour needs this much measuring before it is rated (a drop is always shown).</summary>
    public const double MinMeasuredSeconds = 300;

    public DateTime LocalStart { get; init; }
    public double MonitoredSeconds { get; init; }
    /// <summary>Typical ping minus each server's usual, averaged over servers; null when no server had enough replies.</summary>
    public double? PingAboveUsualMs { get; init; }
    public double? VariationMs { get; init; }
    /// <summary>Over servers that answered in this hour (a server that never answers is filtering pings, not losing them), drops left out.</summary>
    public double? LossPercent { get; init; }
    public int Drops { get; init; }
    public double OfflineSeconds { get; init; }

    public bool HasDrop => Drops > 0 || OfflineSeconds > 0;

    public DiaryLevel LevelFor(DiaryMetric metric)
    {
        bool thin = MonitoredSeconds < MinMeasuredSeconds;
        switch (metric)
        {
            case DiaryMetric.Drops:
                return HasDrop ? DiaryLevel.Poor : thin ? DiaryLevel.Thin : DiaryLevel.Good;
            case DiaryMetric.PingAboveUsual:
                return thin || PingAboveUsualMs is not double p ? DiaryLevel.Thin : p >= 50 ? DiaryLevel.Poor : p >= 15 ? DiaryLevel.Fair : DiaryLevel.Good;
            case DiaryMetric.Variation:
                return thin || VariationMs is not double v ? DiaryLevel.Thin : v > 20 ? DiaryLevel.Poor : v > 8 ? DiaryLevel.Fair : DiaryLevel.Good;
            case DiaryMetric.ProbeLoss:
                return thin || LossPercent is not double l ? DiaryLevel.Thin : l >= 3 ? DiaryLevel.Poor : l >= 1 ? DiaryLevel.Fair : DiaryLevel.Good;
            default:
                if (HasDrop) return DiaryLevel.Poor;
                if (thin) return DiaryLevel.Thin;
                var levels = new[] { DiaryMetric.PingAboveUsual, DiaryMetric.Variation, DiaryMetric.ProbeLoss }.Select(LevelFor).Where(l => l != DiaryLevel.Thin).ToList();
                return levels.Count == 0 ? DiaryLevel.Thin : levels.Max();
        }
    }

    /// <summary>The hover text: what was measured in this hour.</summary>
    public string Describe()
    {
        var lines = new List<string> { $"{LocalStart.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture)}-{LocalStart.AddHours(1):HH:mm}", $"Measured {Lexicon.Duration(TimeSpan.FromSeconds(MonitoredSeconds))}" };
        if (HasDrop)
            lines.Add(Drops > 0
                ? $"{Drops} drop{(Drops == 1 ? "" : "s")}: {Lexicon.Duration(TimeSpan.FromSeconds(OfflineSeconds))} without internet"
                : $"No internet for {Lexicon.Duration(TimeSpan.FromSeconds(OfflineSeconds))} (a drop that started earlier)");
        if (MonitoredSeconds < MinMeasuredSeconds)
        {
            lines.Add("Too little measured to rate this hour.");
            return string.Join("\n", lines);
        }
        var parts = new List<string>();
        if (PingAboveUsualMs is double p) parts.Add(Math.Abs(p) < 1 ? "ping at its usual level" : p > 0 ? $"ping {p:0} ms above usual" : $"ping {-p:0} ms below usual");
        if (VariationMs is double v) parts.Add($"variation {v:0.#} ms");
        if (LossPercent is double l) parts.Add($"probe loss {l:0.#}%");
        lines.Add(parts.Count > 0 ? string.Join(" · ", parts) : "No reference server answered enough to rate ping.");
        return string.Join("\n", lines);
    }
}

public sealed record DiaryDay(DateTime LocalDate, IReadOnlyList<DiaryCell?> Hours);

/// <summary>One drop of the internet, as the diary lists it.</summary>
public sealed record DiaryDrop(DateTime StartUtc, DateTime EndUtc, bool Ongoing, bool EndedUnseen, bool BeyondRouter)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public string DurationText => Ongoing ? $"ongoing, {Lexicon.Duration(Duration)} so far"
        : EndedUnseen ? $"at least {Lexicon.Duration(Duration)} (Lagwise stopped before it came back)"
        : Lexicon.Duration(Duration);

    public string WhereText => BeyondRouter
        ? "Beyond your router: your modem or your provider"
        : "The router was silent too: cable, Wi-Fi or the router itself";
}

/// <summary>The diary for a range of days: the hour-by-hour grid, the drops, and what it adds up to.</summary>
public sealed class DiaryView
{
    public IReadOnlyList<DiaryDay> Days { get; init; } = Array.Empty<DiaryDay>();
    /// <summary>Newest first.</summary>
    public IReadOnlyList<DiaryDrop> Drops { get; init; } = Array.Empty<DiaryDrop>();
    public double MonitoredSeconds { get; init; }
    public int DaysMeasured { get; init; }
    public double OfflineSeconds { get; init; }
    public double? LossPercent { get; init; }
    /// <summary>The rated hour with ping furthest above usual, or null.</summary>
    public DiaryCell? WorstPing { get; init; }
    public string Summary { get; init; } = "";
    public bool IsEmpty => MonitoredSeconds <= 0;
}

/// <summary>Turns the stored hours into the diary grid, in local time, and says what they show.</summary>
public static class DiaryAnalysis
{
    /// <summary>A server needs this many replies in an hour before its ping in that hour is used.</summary>
    public const int MinRepliesForPing = 30;
    /// <summary>Hours with enough replies a server needs before its "usual" is known.</summary>
    public const int MinHoursForUsual = 3;

    public static DiaryView Build(DiarySnapshot snapshot, int days, DateTime nowUtc, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = toLocal ?? (t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime());
        var today = local(nowUtc).Date;
        var firstDay = today.AddDays(-(Math.Max(1, days) - 1));

        // each server's usual ping: the median of its hourly typical pings over everything the diary holds
        var usual = snapshot.Hours.SelectMany(h => h.Servers).Where(s => s.Replies >= MinRepliesForPing)
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinHoursForUsual)
            .ToDictionary(g => g.Key, g => StatisticalCalculator.CalculateMedian(g.Select(s => s.MedianMs).ToList()), StringComparer.OrdinalIgnoreCase);

        var drops = snapshot.Outages.Select(o => new DiaryDrop(o.StartUtc, o.EndUtc ?? Max(nowUtc, o.StartUtc), o.EndUtc == null, o.EndedUnseen, o.BeyondRouter)).ToList();

        var cells = new Dictionary<DateTime, DiaryCell>();
        foreach (var h in snapshot.Hours)
        {
            var start = local(h.StartUtc);
            if (start.Date < firstDay || start.Date > today || h.MonitoredSeconds <= 0) continue;
            var cell = CellFor(h, start, usual, drops);
            var key = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
            cells[key] = cells.TryGetValue(key, out var other) ? Combine(other, cell) : cell;   // two UTC hours in one local hour (clock change)
        }

        var dayRows = new List<DiaryDay>();
        for (var d = today; d >= firstDay; d = d.AddDays(-1))
            dayRows.Add(new DiaryDay(d, Enumerable.Range(0, 24).Select(hour => cells.TryGetValue(d.AddHours(hour), out var c) ? c : null).ToList()));

        var all = cells.Values.ToList();
        var inRange = drops.Where(dr => local(dr.StartUtc).Date >= firstDay && local(dr.StartUtc).Date <= today).OrderByDescending(dr => dr.StartUtc).ToList();
        double monitored = all.Sum(c => c.MonitoredSeconds);
        double offline = all.Sum(c => c.OfflineSeconds);
        double? loss = WeightedLoss(snapshot.Hours.Where(h => { var s = local(h.StartUtc); return s.Date >= firstDay && s.Date <= today; }));
        var rated = all.Where(c => c.MonitoredSeconds >= DiaryCell.MinMeasuredSeconds && c.PingAboveUsualMs.HasValue).ToList();
        var worst = rated.OrderByDescending(c => c.PingAboveUsualMs).FirstOrDefault();

        return new DiaryView
        {
            Days = dayRows,
            Drops = inRange,
            MonitoredSeconds = monitored,
            DaysMeasured = all.Select(c => c.LocalStart.Date).Distinct().Count(),
            OfflineSeconds = offline,
            LossPercent = loss,
            WorstPing = worst,
            Summary = Summarize(days, monitored, all, inRange, offline, loss, worst, rated)
        };
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DiaryCell CellFor(DiaryHour h, DateTime localStart, IReadOnlyDictionary<string, double> usual, IReadOnlyList<DiaryDrop> drops)
    {
        var hourEnd = h.StartUtc.AddHours(1);
        var rich = h.Servers.Where(s => s.Replies >= MinRepliesForPing).ToList();
        var withUsual = rich.Where(s => usual.ContainsKey(s.Key)).ToList();

        double? above = withUsual.Count == 0 ? null : withUsual.Sum(s => (s.MedianMs - usual[s.Key]) * s.Replies) / withUsual.Sum(s => s.Replies);
        double? variation = rich.Count == 0 ? null : rich.Sum(s => s.VariationMs * s.Replies) / rich.Sum(s => s.Replies);

        double offline = drops.Sum(d =>
        {
            var from = d.StartUtc > h.StartUtc ? d.StartUtc : h.StartUtc;
            var to = d.EndUtc < hourEnd ? d.EndUtc : hourEnd;
            return to > from ? (to - from).TotalSeconds : 0;
        });

        return new DiaryCell
        {
            LocalStart = localStart,
            MonitoredSeconds = h.MonitoredSeconds,
            PingAboveUsualMs = above.HasValue ? Math.Round(above.Value, 1) : null,
            VariationMs = variation.HasValue ? Math.Round(variation.Value, 1) : null,
            LossPercent = WeightedLoss(new[] { h }),
            Drops = drops.Count(d => d.StartUtc >= h.StartUtc && d.StartUtc < hourEnd),
            OfflineSeconds = Math.Min(3600, offline)
        };
    }

    /// <summary>Unanswered probes among servers that answered at all in their hour.</summary>
    private static double? WeightedLoss(IEnumerable<DiaryHour> hours)
    {
        var answering = hours.SelectMany(h => h.Servers).Where(s => s.Replies > 0).ToList();
        int probes = answering.Sum(s => s.Probes);
        return probes == 0 ? null : Math.Round(answering.Sum(s => s.Probes - s.Replies) * 100.0 / probes, 2);
    }

    private static DiaryCell Combine(DiaryCell a, DiaryCell b)
    {
        double wa = Math.Max(1, a.MonitoredSeconds), wb = Math.Max(1, b.MonitoredSeconds);
        static double? Avg(double? x, double wx, double? y, double wy) =>
            x.HasValue && y.HasValue ? (x * wx + y * wy) / (wx + wy) : x ?? y;
        return new DiaryCell
        {
            LocalStart = a.LocalStart,
            MonitoredSeconds = a.MonitoredSeconds + b.MonitoredSeconds,
            PingAboveUsualMs = Avg(a.PingAboveUsualMs, wa, b.PingAboveUsualMs, wb),
            VariationMs = Avg(a.VariationMs, wa, b.VariationMs, wb),
            LossPercent = Avg(a.LossPercent, wa, b.LossPercent, wb),
            Drops = a.Drops + b.Drops,
            OfflineSeconds = a.OfflineSeconds + b.OfflineSeconds
        };
    }

    public static string Hours(double seconds) =>
        seconds < 3600 ? $"{Math.Max(1, Math.Round(seconds / 60)):0} min" : seconds < 36000 ? $"{seconds / 3600:0.#} h" : $"{seconds / 3600:0} h";

    private static string Summarize(int days, double monitored, IReadOnlyList<DiaryCell> cells, IReadOnlyList<DiaryDrop> drops, double offline,
        double? loss, DiaryCell? worst, IReadOnlyList<DiaryCell> rated)
    {
        if (monitored <= 0)
            return "Nothing measured in these days yet. The diary fills in by itself while Lagwise runs, recording or not, and keeps the last 31 days.";

        int daysMeasured = cells.Select(c => c.LocalStart.Date).Distinct().Count();
        var parts = new List<string>
        {
            days == 1 ? $"Today Lagwise measured for {Hours(monitored)}."
                : $"Over the last {days} days Lagwise measured for {Hours(monitored)}, on {daysMeasured} of them."
        };

        if (drops.Count == 0)
            parts.Add("No drops: the internet answered whenever Lagwise was measuring.");
        else
        {
            int beyond = drops.Count(d => d.BeyondRouter), local = drops.Count - beyond;
            string where = local == 0 ? (beyond == 1 ? "beyond your router, at your modem or provider" : "all beyond your router, at your modem or provider")
                : beyond == 0 ? (local == 1 ? "with your router silent too, so inside your home network" : "all with your router silent too, so inside your home network")
                : $"{beyond} beyond your router, {local} with the router silent too";
            parts.Add($"{drops.Count} drop{(drops.Count == 1 ? "" : "s")} without internet, {Lexicon.Duration(TimeSpan.FromSeconds(offline))} in total ({where}).");
            if (drops.Any(d => d.EndedUnseen)) parts.Add("Lagwise stopped during a drop, so that one may have lasted longer.");
        }

        if (worst?.PingAboveUsualMs is double w && w >= 15)
            parts.Add($"Ping was furthest above your usual on {worst.LocalStart.ToString("ddd d MMM", CultureInfo.CurrentCulture)} around {worst.LocalStart:HH:00} (+{w:0} ms).");

        var evening = rated.Where(c => TestTrends.IsEvening(c.LocalStart)).ToList();
        var rest = rated.Where(c => !TestTrends.IsEvening(c.LocalStart)).ToList();
        if (evening.Count >= 3 && rest.Count >= 3)
        {
            double e = evening.Sum(c => c.PingAboveUsualMs!.Value * c.MonitoredSeconds) / evening.Sum(c => c.MonitoredSeconds);
            double r = rest.Sum(c => c.PingAboveUsualMs!.Value * c.MonitoredSeconds) / rest.Sum(c => c.MonitoredSeconds);
            if (e - r >= 8) parts.Add($"Evenings (18:00 to midnight) run {e - r:0} ms above the rest of the day on average, typical of a provider that is busy at peak time.");
            else if (r - e >= 8) parts.Add($"Daytime runs {r - e:0} ms above the evenings on average.");
            else parts.Add("Evenings look like the rest of the day.");
        }

        if (loss is double l)
            parts.Add(l >= 1 ? $"Probe loss outside the drops: {l:0.#}%, above the 1% where games start to feel it." : $"Probe loss outside the drops was low ({l:0.##}%).");
        return string.Join(" ", parts);
    }

    /// <summary>One line for Copy summary: the last days in numbers, without times or addresses. Null when nothing was measured.</summary>
    public static string? SummaryLine(DiaryView view, int days)
    {
        if (view.IsEmpty) return null;
        string drops = view.Drops.Count == 0 ? "no drops" : $"{view.Drops.Count} drop{(view.Drops.Count == 1 ? "" : "s")} ({Lexicon.Duration(TimeSpan.FromSeconds(view.OfflineSeconds))} without internet)";
        string ping = view.WorstPing?.PingAboveUsualMs is double w && w >= 15 ? $", ping up to {w:0} ms above usual at worst" : "";
        string loss = view.LossPercent is double l ? $", probe loss {l:0.##}%" : "";
        return $"Last {days} days (Lagwise measured {Hours(view.MonitoredSeconds)}): {drops}{ping}{loss}.";
    }

    /// <summary>Lines for the exported report: the summary, one line per day, and the drops.</summary>
    public static IReadOnlyList<string> ReportLines(DiaryView view, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = toLocal ?? (t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime());
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string> { view.Summary, "" };
        if (view.IsEmpty) return lines;

        lines.Add("| Day | Measured | Drops | Without internet | Probe loss | Worst hour (ping above usual) |");
        lines.Add("|---|---|---|---|---|---|");
        foreach (var day in view.Days)
        {
            var cells = day.Hours.Where(c => c != null).Select(c => c!).ToList();
            if (cells.Count == 0) continue;
            var worst = cells.Where(c => c.MonitoredSeconds >= DiaryCell.MinMeasuredSeconds && c.PingAboveUsualMs.HasValue).OrderByDescending(c => c.PingAboveUsualMs).FirstOrDefault();
            double weight = cells.Where(c => c.LossPercent.HasValue).Sum(c => c.MonitoredSeconds);
            string lossText = weight > 0 ? string.Format(inv, "{0:0.##}%", cells.Where(c => c.LossPercent.HasValue).Sum(c => c.LossPercent!.Value * c.MonitoredSeconds) / weight) : "-";
            lines.Add(string.Format(inv, "| {0} | {1} | {2} | {3} | {4} | {5} |",
                day.LocalDate.ToString("ddd yyyy-MM-dd", inv), Hours(cells.Sum(c => c.MonitoredSeconds)), cells.Sum(c => c.Drops),
                cells.Sum(c => c.OfflineSeconds) > 0 ? Lexicon.Duration(TimeSpan.FromSeconds(cells.Sum(c => c.OfflineSeconds))) : "-", lossText,
                worst == null ? "-" : string.Format(inv, "{0:HH:00} ({1:+0;-0;0} ms)", worst.LocalStart, worst.PingAboveUsualMs)));
        }
        if (view.Drops.Count > 0)
        {
            lines.Add("");
            lines.Add("Drops, newest first:");
            lines.Add("");
            foreach (var d in view.Drops.Take(50))
                lines.Add($"- {local(d.StartUtc).ToString("yyyy-MM-dd HH:mm:ss", inv)}: {d.DurationText}. {d.WhereText}.");
        }
        return lines;
    }
}
