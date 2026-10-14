using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.App.ViewModels;

/// <summary>One bar of the time-of-day chart. Level picks the colour in XAML: Good, Warn, Bad or None (not enough data).</summary>
public sealed class HourBar
{
    public const double MaxHeight = 84;

    public HourBar(HourPattern h, double scaleMs)
    {
        Label = h.Hour % 3 == 0 ? h.Hour.ToString("00") : "";
        double badness = Math.Max(0, h.DeltaMs ?? 0) + 10 * h.LossPercent;
        Height = h.HasEnoughData ? Math.Max(4, Math.Min(1, badness / scaleMs) * MaxHeight) : 3;
        Level = !h.HasEnoughData ? "None"
            : h.LossPercent >= 2 || (h.DeltaMs ?? 0) >= 15 ? "Bad"
            : h.LossPercent >= ConnectionPatterns.NoticeableLossPercent || (h.DeltaMs ?? 0) >= ConnectionPatterns.NoticeableDeltaMs ? "Warn"
            : "Good";
        string range = ConnectionPatterns.HourRange(h.Hour);
        Tip = h.SampleCount == 0 ? $"{range}: nothing recorded in this hour yet"
            : !h.HasEnoughData
            ? $"{range}: not enough data ({h.SampleCount} probes; about {ConnectionPatterns.MinSamplesPerHour} needed)"
            : $"{range}\nPing {(h.DeltaMs is double d ? (d >= 0 ? $"{d:0.#} ms above" : $"{-d:0.#} ms below") : "-")} your usual\nProbe loss {h.LossPercent:0.#}%\n" +
              $"{h.SampleCount:N0} probes from {h.RecordingCount} recording{(h.RecordingCount == 1 ? "" : "s")}" +
              (h.LagMarkers > 0 ? $"\nYou marked lag {h.LagMarkers} time{(h.LagMarkers == 1 ? "" : "s")}" : "");
    }

    public string Label { get; }
    public double Height { get; }
    public string Level { get; }
    public string Tip { get; }
}

/// <summary>History: "When is my connection worst?", pooled over every saved recording.</summary>
public sealed class PatternsViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _summary = "Looks for hours of the day when your ping is higher than usual or probes get lost, using every recording on this PC.";
    private string _progress = "";

    public PatternsViewModel(NetPulseHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => !IsRunning);
    }

    public ObservableCollection<HourBar> Bars { get; } = new();
    public RelayCommand AnalyzeCommand { get; }
    public bool HasBars => Bars.Count > 0;
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ButtonText => HasBars ? "Analyse again" : "Analyse my recordings";

    public async Task AnalyzeAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var patterns = new ConnectionPatterns();
                var list = _host.Sessions.List();
                for (int i = 0; i < list.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    int n = i + 1;
                    _dispatcher.InvokeAsync(() => Progress = $"Reading recording {n} of {list.Count}…");
                    try
                    {
                        // one recording at a time: only the per-hour numbers are kept
                        var s = _host.Sessions.Load(list[i].Id);
                        if (s != null) patterns.Add(s);
                    }
                    catch
                    {
                        // a damaged file is skipped; the recordings list already flags it
                    }
                }
                return patterns.Build();
            }, cts.Token);

            if (cts.IsCancellationRequested) return;
            double scale = Math.Max(15, result.Hours.Where(h => h.HasEnoughData).Select(h => Math.Max(0, h.DeltaMs ?? 0) + 10 * h.LossPercent).DefaultIfEmpty(0).Max());
            Bars.Clear();
            foreach (var h in result.Hours) Bars.Add(new HourBar(h, scale));
            Summary = result.Summary;
            Progress = $"{result.RecordingCount} recording{(result.RecordingCount == 1 ? "" : "s")}, {result.SampleCount:N0} reference probes. Hours are your PC's local time. Hover a bar for details.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Summary = $"The recordings could not be analysed: {ex.Message}";
            Progress = "";
        }
        finally
        {
            if (_cts == cts) IsRunning = false;
            OnPropertyChanged(nameof(HasBars));
            OnPropertyChanged(nameof(ButtonText));
        }
    }
}
