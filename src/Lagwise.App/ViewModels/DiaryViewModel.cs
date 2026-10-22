using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using Lagwise.Core;
using Lagwise.Core.Services;

namespace Lagwise.App.ViewModels;

/// <summary>One hour of the diary grid. Level: Good / Fair / Poor / Thin (too little measured) / None (not measured) / Future.</summary>
public sealed record DiaryCellVm(string Level, string Tip, double Opacity);

public sealed record DiaryRowVm(string Label, IReadOnlyList<DiaryCellVm> Cells);

public sealed record DropRowVm(string When, string Duration, string Where, bool BeyondRouter);

public sealed record DiaryChoice(string Label, int Value);

/// <summary>
/// Connection diary: how the connection did, hour by hour, whenever Lagwise ran, over the last days; every drop; and a
/// 24-hour strip for the Overview. Rebuilt every minute while the page is open, every few minutes otherwise.
/// </summary>
public sealed class DiaryViewModel : ViewModelBase
{
    private readonly LagwiseHost _host;
    private readonly DispatcherTimer _timer;
    private bool _isVisible;
    private int _days = 14;
    private int _metric;
    private string _summary = "", _measured = "-", _measuredCaption = "", _drops = "-", _dropsCaption = "", _loss = "-", _lossCaption = "", _worst = "-", _worstCaption = "";
    private string _last24Text = "", _moreDrops = "";
    private bool _isEmpty = true;

    public DiaryViewModel(LagwiseHost host, Dispatcher dispatcher)
    {
        _host = host;
        HourLabels = Enumerable.Range(0, 24).Select(h => h % 3 == 0 ? h.ToString("00", CultureInfo.InvariantCulture) : "").ToList();
        ClearCommand = new RelayCommand(Clear, () => !_isEmpty);
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
        {
            // the Overview strip changes slowly: every 5 minutes is enough unless the diary is on screen
            if (_isVisible || DateTime.UtcNow.Minute % 5 == 0) Refresh();
        }, dispatcher);
        Refresh();
    }

    public IReadOnlyList<DiaryChoice> RangeChoices { get; } = new[] { new DiaryChoice("Last 7 days", 7), new DiaryChoice("Last 14 days", 14), new DiaryChoice("Last 30 days", 30) };
    public IReadOnlyList<DiaryChoice> MetricChoices { get; } = new[]
    {
        new DiaryChoice("Overall", (int)DiaryMetric.Overall), new DiaryChoice("Ping above usual", (int)DiaryMetric.PingAboveUsual),
        new DiaryChoice("Variation", (int)DiaryMetric.Variation), new DiaryChoice("Probe loss", (int)DiaryMetric.ProbeLoss), new DiaryChoice("Drops", (int)DiaryMetric.Drops)
    };

    public int Days { get => _days; set { if (SetProperty(ref _days, value)) Refresh(); } }
    public int Metric { get => _metric; set { if (SetProperty(ref _metric, value)) Refresh(); } }

    public IReadOnlyList<string> HourLabels { get; }
    public ObservableCollection<DiaryRowVm> Rows { get; } = new();
    public ObservableCollection<DiaryCellVm> Last24 { get; } = new();
    public ObservableCollection<DropRowVm> Drops { get; } = new();

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string MeasuredText { get => _measured; private set => SetProperty(ref _measured, value); }
    public string MeasuredCaption { get => _measuredCaption; private set => SetProperty(ref _measuredCaption, value); }
    public string DropsText { get => _drops; private set => SetProperty(ref _drops, value); }
    public string DropsCaption { get => _dropsCaption; private set => SetProperty(ref _dropsCaption, value); }
    public string LossText { get => _loss; private set => SetProperty(ref _loss, value); }
    public string LossCaption { get => _lossCaption; private set => SetProperty(ref _lossCaption, value); }
    public string WorstText { get => _worst; private set => SetProperty(ref _worst, value); }
    public string WorstCaption { get => _worstCaption; private set => SetProperty(ref _worstCaption, value); }
    public string Last24Text { get => _last24Text; private set => SetProperty(ref _last24Text, value); }
    public string MoreDropsText { get => _moreDrops; private set => SetProperty(ref _moreDrops, value); }
    public bool HasMoreDrops => _moreDrops.Length > 0;
    public bool HasDrops => Drops.Count > 0;
    public bool IsEmpty { get => _isEmpty; private set { if (SetProperty(ref _isEmpty, value)) OnPropertyChanged(nameof(HasData)); } }
    public bool HasData => !_isEmpty;
    public RelayCommand ClearCommand { get; }

    /// <summary>The page is on screen: refresh now and every minute.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value) && value) Refresh(); }
    }

    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var snapshot = _host.Diary.Snapshot();
        var view = DiaryAnalysis.Build(snapshot, _days, now);
        var metric = (DiaryMetric)_metric;
        var localNow = DateTime.SpecifyKind(now, DateTimeKind.Utc).ToLocalTime();

        Rows.Clear();
        foreach (var day in view.Days)
        {
            string label = day.LocalDate == localNow.Date ? "Today" : day.LocalDate == localNow.Date.AddDays(-1) ? "Yesterday" : day.LocalDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            Rows.Add(new DiaryRowVm(label, Enumerable.Range(0, 24).Select(h => Cell(day.Hours[h], day.LocalDate.AddHours(h), localNow, metric)).ToList()));
        }

        Summary = view.Summary;
        IsEmpty = view.IsEmpty;
        MeasuredText = view.IsEmpty ? "-" : DiaryAnalysis.Hours(view.MonitoredSeconds);
        MeasuredCaption = view.IsEmpty ? "nothing yet" : $"on {view.DaysMeasured} of {_days} days";
        DropsText = view.Drops.Count.ToString(CultureInfo.CurrentCulture);
        DropsCaption = view.Drops.Count == 0 ? "the internet always answered" : $"{Lexicon.Duration(TimeSpan.FromSeconds(view.OfflineSeconds))} without internet";
        LossText = view.LossPercent is double l ? $"{l:0.##}%" : "-";
        LossCaption = "of probes, outside drops";
        WorstText = view.WorstPing?.PingAboveUsualMs is double w ? (w > 0 ? $"+{w:0} ms" : "usual") : "-";
        WorstCaption = view.WorstPing is { } c ? $"{c.LocalStart.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {c.LocalStart:HH:00}" : "ping above usual";

        Drops.Clear();
        foreach (var d in view.Drops.Take(20))
            Drops.Add(new DropRowVm(DateTime.SpecifyKind(d.StartUtc, DateTimeKind.Utc).ToLocalTime().ToString("ddd d MMM, HH:mm:ss", CultureInfo.CurrentCulture), d.DurationText, d.WhereText, d.BeyondRouter));
        MoreDropsText = view.Drops.Count > 20 ? $"and {view.Drops.Count - 20} earlier drops (all are in the exported report)" : "";
        OnPropertyChanged(nameof(HasDrops));
        OnPropertyChanged(nameof(HasMoreDrops));

        BuildLast24(snapshot, now, localNow);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The last 24 hours, ending with the current one, for the Overview.</summary>
    private void BuildLast24(DiarySnapshot snapshot, DateTime now, DateTime localNow)
    {
        var view = DiaryAnalysis.Build(snapshot, 2, now);
        var cells = view.Days.SelectMany(d => d.Hours.Select((c, h) => (Start: d.LocalDate.AddHours(h), Cell: c))).ToDictionary(x => x.Start, x => x.Cell);
        var currentHour = localNow.Date.AddHours(localNow.Hour);
        Last24.Clear();
        for (int i = 23; i >= 0; i--)
        {
            var start = currentHour.AddHours(-i);
            Last24.Add(Cell(cells.GetValueOrDefault(start), start, localNow, DiaryMetric.Overall));
        }
        var dayCells = cells.Where(kv => kv.Key > currentHour.AddHours(-24) && kv.Value != null).Select(kv => kv.Value!).ToList();
        int drops = dayCells.Sum(c => c.Drops);
        double measured = dayCells.Sum(c => c.MonitoredSeconds);
        double? worst = dayCells.Where(c => c.MonitoredSeconds >= DiaryCell.MinMeasuredSeconds && c.PingAboveUsualMs.HasValue).Select(c => c.PingAboveUsualMs).Max();
        Last24Text = measured <= 0 ? "Nothing measured in the last 24 hours yet."
            : $"Measured {DiaryAnalysis.Hours(measured)}. " + (drops == 0 ? "No drops." : $"{drops} drop{(drops == 1 ? "" : "s")}, {Lexicon.Duration(TimeSpan.FromSeconds(dayCells.Sum(c => c.OfflineSeconds)))} without internet.")
              + (worst is double w && w >= 15 ? $" Ping up to {w:0} ms above usual." : "");
    }

    private static DiaryCellVm Cell(DiaryCell? c, DateTime localStart, DateTime localNow, DiaryMetric metric)
    {
        if (localStart > localNow) return new DiaryCellVm("Future", "", 1);
        if (c == null) return new DiaryCellVm("None", $"{localStart.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture)}: not measured (Lagwise was not running).", 1);
        var level = c.LevelFor(metric);
        // a fuller hour is drawn stronger, so ten measured minutes do not look like a whole hour
        double opacity = level == DiaryLevel.Thin ? 1 : 0.45 + 0.55 * Math.Min(1, c.MonitoredSeconds / 3600);
        return new DiaryCellVm(level.ToString(), c.Describe(), opacity);
    }

    private void Clear()
    {
        var answer = MessageBox.Show("Forget the whole connection diary?\n\nEvery hour and drop it holds is removed from this PC and cannot be brought back. It starts again from now.",
            "Clear the diary", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _host.Diary.Clear();
        Refresh();
    }
}
