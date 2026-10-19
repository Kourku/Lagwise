using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NetPulse.Core;
using NetPulse.Core.Services;

namespace NetPulse.App.ViewModels;

/// <summary>
/// One bar of a results chart. Level picks the colour: Good / Warn / Bad by result, Evening / Day by time of day.
/// Ping seen in real use is an outline (faint fill, solid edge), because it was not a test.
/// </summary>
public sealed record TestBar(string Label, string ValueText, double Height, string Level, string Tip, double Opacity, double Edge);

/// <summary>One line of the recent results list.</summary>
public sealed record TestRow(string When, string Kind, string Main, string Detail);

/// <summary>"Your results over time" for one page: a bar per result, the latest few as text, and what they add up to.</summary>
public sealed class TestPanel : ViewModelBase
{
    public const int MaxBars = 12;
    public const int MaxRows = 5;
    private const double ChartHeight = 84;

    private readonly TestHistory _tests;
    private readonly TestKind[] _kinds;
    private readonly Func<IReadOnlyList<TestRecord>, string> _summarize;
    private readonly string _one, _many;
    private string _summary = "", _countText = "";

    /// <param name="one">What one record is called ("speed test"), and <paramref name="many"/> several.</param>
    public TestPanel(TestHistory tests, string one, string many, Func<IReadOnlyList<TestRecord>, string> summarize, ToastViewModel toast, params TestKind[] kinds)
    {
        _tests = tests;
        _kinds = kinds;
        _summarize = summarize;
        _one = one;
        _many = many;
        ClearCommand = new RelayCommand(() =>
        {
            int count = _tests.Of(_kinds).Count;
            var answer = MessageBox.Show($"Forget {(count == 1 ? $"the stored {_one}" : $"all {count} stored {_many}")}?\n\nThey are removed from this PC and cannot be brought back.",
                "Clear results", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            int n = _tests.Clear(_kinds);
            toast.Show($"Forgot {n} stored result{(n == 1 ? "" : "s")}.");
        }, () => HasAny);
    }

    public ObservableCollection<TestBar> Bars { get; } = new();
    public ObservableCollection<TestRow> Recent { get; } = new();
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }
    public bool HasAny => Recent.Count > 0;
    public bool IsEmpty => Recent.Count == 0;
    public RelayCommand ClearCommand { get; }

    public void Refresh()
    {
        var all = _tests.Of(_kinds);   // newest first
        Bars.Clear();
        Recent.Clear();

        var shown = all.Take(MaxBars).Reverse().ToList();
        double max = shown.Count == 0 ? 1 : Math.Max(1, shown.Max(Value));
        if (_kinds.Contains(TestKind.Bufferbloat)) max = Math.Max(max, 60);   // a few ms never fill the chart
        foreach (var r in shown)
        {
            var local = Local(r.T);
            bool seen = r.Kind == TestKind.RealLoad;
            Bars.Add(new TestBar(local.ToString("ddd HH:mm", CultureInfo.CurrentCulture),
                r.Kind == TestKind.Speed ? $"{r.DownMbps:0}" : $"+{Math.Max(0, r.WorstRiseMs):0} ms",
                Math.Max(3, Value(r) / max * ChartHeight), Level(r),
                $"{local:ddd d MMM, HH:mm} · {KindLabel(r)}{(r.Connection.Length > 0 ? " · " + r.Connection : "")}\n{Headline(r)}",
                seen ? 0.22 : 1, seen ? 1.5 : 0));
        }
        foreach (var r in all.Take(MaxRows))
        {
            var local = Local(r.T);
            string when = $"{local:ddd d MMM, HH:mm}" + (TestTrends.IsEvening(local) ? " · evening" : "") + (r.Connection.Length > 0 ? " · " + r.Connection : "");
            Recent.Add(new TestRow(when, KindLabel(r), Headline(r), Detail(r)));
        }

        CountText = all.Count == 0 ? "" : $"{all.Count} {(all.Count == 1 ? _one : _many)} kept on this PC" + (all.Count > MaxBars ? $", the latest {MaxBars} in the chart" : "");
        Summary = string.Join(" ", _kinds.Select(k => _summarize(all.Where(r => r.Kind == k).ToList())).Where(s => s.Length > 0));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(IsEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    private static double Value(TestRecord r) => r.Kind == TestKind.Speed ? r.DownMbps : Math.Max(0, r.WorstRiseMs);

    private static string Level(TestRecord r) => r.Kind switch
    {
        TestKind.Speed => TestTrends.IsEvening(Local(r.T)) ? "Evening" : "Day",
        _ => r.WorstRiseMs <= PingUnderLoad.SteadyRiseMs ? "Good" : r.WorstRiseMs <= 60 ? "Warn" : "Bad"
    };

    public static string KindLabel(TestRecord r) => r.Kind switch
    {
        TestKind.Speed => "Speed test",
        TestKind.Bufferbloat => "Bufferbloat test",
        _ => "Seen in real use"
    };

    public static string Headline(TestRecord r) => r.Kind switch
    {
        TestKind.Speed => $"{r.DownMbps:0.#} Mbps down · {r.UpMbps:0.#} Mbps up",
        TestKind.Bufferbloat => $"Grade {r.Grade} · ping +{Math.Max(r.DownDeltaMs, 0):0} ms downloading, +{Math.Max(r.UpDeltaMs, 0):0} ms uploading",
        _ => $"Ping {(r.RiseMs >= 0 ? "+" : "")}{r.RiseMs:0} ms during heavy traffic ({r.RestMs:0} ms before, {r.LoadedMs:0} ms during)"
    };

    private static string Detail(TestRecord r) => r.Kind switch
    {
        TestKind.Speed => "",
        TestKind.Bufferbloat => $"{r.RestMs:0} ms at rest" + (r.DownMbps > 0 ? $"; measured at {r.DownMbps:0} Mbps down and {r.UpMbps:0} Mbps up" : ""),
        _ => $"{Lexicon.Duration(TimeSpan.FromSeconds(r.Seconds))} at about {r.DownMbps:0} Mbps down and {r.UpMbps:0.#} Mbps up on average"
             + (r.LossPercent >= 5 ? $"; {r.LossPercent:0}% of pings unanswered" : "") + ". Not a test: the line may not have been full."
    };
}

/// <summary>The stored results behind the Speed test and Bufferbloat pages, refreshed whenever a result is added or cleared.</summary>
public sealed class TestResultsViewModel
{
    public TestResultsViewModel(NetPulseHost host, Dispatcher dispatcher, ToastViewModel toast)
    {
        Speed = new TestPanel(host.Tests, "speed test", "speed tests", r => TestTrends.Speed(r), toast, TestKind.Speed);
        Bufferbloat = new TestPanel(host.Tests, "result", "results",r => r.Count == 0 ? "" : r[0].Kind == TestKind.RealLoad ? TestTrends.RealLoad(r) : TestTrends.Bufferbloat(r),
            toast, TestKind.Bufferbloat, TestKind.RealLoad);
        host.Tests.Changed += (_, _) => dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public TestPanel Speed { get; }
    /// <summary>Bufferbloat tests and ping seen during real heavy traffic, together.</summary>
    public TestPanel Bufferbloat { get; }

    public void Refresh()
    {
        Speed.Refresh();
        Bufferbloat.Refresh();
    }
}
