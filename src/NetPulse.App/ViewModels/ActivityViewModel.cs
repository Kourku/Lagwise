using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using NetPulse.App.Controls;
using NetPulse.Core.Services;

namespace NetPulse.App.ViewModels;

public sealed record AdapterOption(string Id, string Label);
public sealed record WindowOption(string Label, int Seconds);

/// <summary>A row of "Programs online now".</summary>
public sealed record ProgramRow(string Name, string Kind, string Connections, bool CanUseALot);

/// <summary>
/// Network activity page: throughput of one network adapter, second by second, like Task Manager's Performance tab.
/// Refreshes once a second while the page is on screen; the host samples the counters all the time, so the history
/// is already there when the page is opened.
/// </summary>
public sealed class ActivityViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private readonly Func<string?> _activeAdapterId;
    private readonly DispatcherTimer _timer;
    private bool _isVisible;
    private string? _selectedId;
    private int _windowSeconds = 60;
    private IReadOnlyList<ActivityPoint> _points = Array.Empty<ActivityPoint>();
    private IReadOnlyList<PingPoint> _pings = Array.Empty<PingPoint>();
    private bool _paused, _mirrored, _showPing = true, _programsBusy;
    private DateTime _programsAt;
    private string _programsSummary = "Looking for programs with internet connections...";

    public ActivityViewModel(NetPulseHost host, Dispatcher dispatcher, Func<string?> activeAdapterId)
    {
        _host = host;
        _activeAdapterId = activeAdapterId;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        _timer.Stop();
    }

    public ObservableCollection<AdapterOption> Adapters { get; } = new();
    public IReadOnlyList<WindowOption> Windows { get; } = new WindowOption[] { new("Last minute", 60), new("Last 5 minutes", 300), new("Last 15 minutes", 900) };

    /// <summary>Set by the main view model: the page is refreshed only while it is on screen.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            if (value) { Refresh(); _timer.Start(); } else _timer.Stop();
        }
    }

    public string? SelectedAdapterId
    {
        get => _selectedId;
        set { if (SetProperty(ref _selectedId, value)) Refresh(); }
    }

    public int WindowSeconds
    {
        get => _windowSeconds;
        set { if (SetProperty(ref _windowSeconds, value)) Refresh(); }
    }

    public IReadOnlyList<ActivityPoint> Points { get => _points; private set => SetProperty(ref _points, value); }
    public IReadOnlyList<PingPoint> PingPoints { get => _pings; private set => SetProperty(ref _pings, value); }

    /// <summary>Freezes the graph and numbers for a closer look; the host keeps sampling, so nothing is lost.</summary>
    public bool IsPaused { get => _paused; set { if (SetProperty(ref _paused, value)) { OnPropertyChanged(nameof(PauseText)); if (!value) Refresh(); } } }
    public string PauseText => _paused ? "Resume" : "Pause";
    public RelayCommand TogglePauseCommand => _togglePause ??= new RelayCommand(() => IsPaused = !IsPaused);
    private RelayCommand? _togglePause;
    /// <summary>Download above the middle line, upload below.</summary>
    public bool Mirrored { get => _mirrored; set => SetProperty(ref _mirrored, value); }
    /// <summary>Reference ping drawn over the traffic: shows bufferbloat as it happens.</summary>
    public bool ShowPing { get => _showPing; set { if (SetProperty(ref _showPing, value)) Refresh(); } }

    public ObservableCollection<ProgramRow> Programs { get; } = new();
    public string ProgramsSummary { get => _programsSummary; private set => SetProperty(ref _programsSummary, value); }

    // ---- readouts (value and unit apart, for the big numbers)
    public string DownValue { get; private set; } = "0";
    public string DownUnit { get; private set; } = "Mbps";
    public string UpValue { get; private set; } = "0";
    public string UpUnit { get; private set; } = "Mbps";
    public string DownStats { get; private set; } = "";
    public string UpStats { get; private set; } = "";
    public string ReceivedTotal { get; private set; } = "-";
    public string SentTotal { get; private set; } = "-";
    public string TotalsCaption { get; private set; } = "";
    public string PacketsText { get; private set; } = "-";
    public string PacketsCaption { get; private set; } = "";
    public string ErrorsText { get; private set; } = "-";
    public string ErrorsCaption { get; private set; } = "";
    public double Utilization { get; private set; }
    public string UtilizationText { get; private set; } = "";
    public string AdapterText { get; private set; } = "";
    public bool HasAdapter { get; private set; }

    private void Refresh()
    {
        RefreshProgramsSoon();
        if (_paused) return;
        var all = _host.Activity.Snapshot();
        SyncAdapters(all);
        var a = all.FirstOrDefault(x => string.Equals(x.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        HasAdapter = a != null;
        if (a == null) { Points = Array.Empty<ActivityPoint>(); NotifyAll(); return; }

        var now = a.Latest?.T ?? DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_windowSeconds);
        Points = a.History;
        PingPoints = _showPing ? PingSeries(now.AddSeconds(-_windowSeconds - 2)) : Array.Empty<PingPoint>();
        (DownValue, DownUnit) = Split(a.Latest?.RxBps ?? 0);
        (UpValue, UpUnit) = Split(a.Latest?.TxBps ?? 0);
        string span = _windowSeconds >= 120 ? $"{_windowSeconds / 60} min" : $"{_windowSeconds} s";
        DownStats = $"Peak {AdapterActivityMonitor.FormatBps(a.PeakRx(window, now))} · average {AdapterActivityMonitor.FormatBps(a.AverageRx(window, now))} (last {span})";
        UpStats = $"Peak {AdapterActivityMonitor.FormatBps(a.PeakTx(window, now))} · average {AdapterActivityMonitor.FormatBps(a.AverageTx(window, now))} (last {span})";
        ReceivedTotal = AdapterActivityMonitor.FormatBytes(a.BytesReceivedSinceStart);
        SentTotal = AdapterActivityMonitor.FormatBytes(a.BytesSentSinceStart);
        TotalsCaption = $"since {a.WatchingSinceUtc.ToLocalTime():HH:mm}";
        PacketsText = a.Latest is { } l ? $"{l.RxPps + l.TxPps:0}" : "-";
        PacketsCaption = a.Latest is { } l2 ? $"{l2.RxPps:0} in, {l2.TxPps:0} out, right now" : "";
        ErrorsText = $"{a.ErrorsSinceStart + a.DiscardsSinceStart:N0}";
        ErrorsCaption = a.ErrorsSinceStart + a.DiscardsSinceStart == 0
            ? "None since NetPulse started. Good."
            : $"{a.ErrorsSinceStart:N0} errors, {a.DiscardsSinceStart:N0} dropped since NetPulse started. A few drops are normal; errors that keep rising point at a cable, port or driver.";
        Utilization = a.Utilization ?? 0;
        UtilizationText = a.Utilization is double u
            ? $"Using {(u < 0.001 ? "less than 0.1" : (u * 100).ToString("0.#"))}% of the {AdapterActivityMonitor.FormatBps(a.SpeedBps)} link between this PC and {(a.Type.Contains("Wireless") ? "the Wi-Fi access point" : "the router")}. Your internet plan is usually slower than this link."
            : "Windows does not report a link speed for this adapter.";
        AdapterText = $"{a.Name} · {a.Description}";
        NotifyAll();
    }

    /// <summary>Typical ping to the plain-ping reference servers, one value per second.</summary>
    private IReadOnlyList<PingPoint> PingSeries(DateTime fromUtc)
    {
        var refs = _host.Engine.GetCurrentMetrics()
            .Where(m => !m.IsGateway && !m.IsDnsResolver && !m.IsGameEndpoint && m.Protocol == Core.Models.NetworkProtocol.Icmp)
            .Select(m => m.TargetId).ToHashSet();
        return ReferencePing.PerSecond(_host.Engine.GetAllHistory(), refs, fromUtc).Select(p => new PingPoint(p.T, p.Ms)).ToList();
    }

    /// <summary>Reads the socket table every few seconds, off the UI thread (it enumerates every process).</summary>
    private void RefreshProgramsSoon()
    {
        if (_programsBusy || DateTime.UtcNow - _programsAt < TimeSpan.FromSeconds(5)) return;
        _programsBusy = true;
        _programsAt = DateTime.UtcNow;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() => _host.ProgramsOnline()).ContinueWith(t =>
        {
            dispatcher.InvokeAsync(() =>
            {
                _programsBusy = false;
                var list = t.IsCompletedSuccessfully ? t.Result : Array.Empty<OnlineProgram>();
                Programs.Clear();
                foreach (var p in list.Take(12))
                    Programs.Add(new ProgramRow(p.DisplayName, p.KindLabel,
                        $"{p.InternetConnections} connection{(p.InternetConnections == 1 ? "" : "s")} to {p.RemoteHosts} address{(p.RemoteHosts == 1 ? "" : "es")}", p.CanUseALot));
                int heavy = list.Count(p => p.CanUseALot);
                ProgramsSummary = list.Count == 0
                    ? "No program has an internet connection open right now (apart from NetPulse)."
                    : $"{list.Count} program{(list.Count == 1 ? "" : "s")} with internet connections open" +
                      (heavy > 0 ? $"; {heavy} of them often download or upload a lot (marked)." : ".") +
                      (list.Count > 12 ? " Showing the first 12." : "");
            });
        });
    }

    /// <summary>Keeps the adapter list in step; selects the adapter Windows routes internet traffic through, or the first one.</summary>
    private void SyncAdapters(IReadOnlyList<AdapterActivity> all)
    {
        var wanted = all.Select(a => new AdapterOption(a.Id, $"{a.Name} ({a.Description})")).ToList();
        if (!wanted.Select(x => x.Id).SequenceEqual(Adapters.Select(x => x.Id)))
        {
            Adapters.Clear();
            foreach (var o in wanted) Adapters.Add(o);
        }
        if (_selectedId == null || Adapters.All(o => !string.Equals(o.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            string? active = _activeAdapterId();
            _selectedId = Adapters.FirstOrDefault(o => string.Equals(o.Id, active, StringComparison.OrdinalIgnoreCase))?.Id ?? Adapters.FirstOrDefault()?.Id;
            OnPropertyChanged(nameof(SelectedAdapterId));
        }
    }

    private static (string, string) Split(double bps)
    {
        string s = AdapterActivityMonitor.FormatBps(bps);
        int i = s.LastIndexOf(' ');
        return (s[..i], s[(i + 1)..]);
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
        {
            nameof(DownValue), nameof(DownUnit), nameof(UpValue), nameof(UpUnit), nameof(DownStats), nameof(UpStats),
            nameof(ReceivedTotal), nameof(SentTotal), nameof(TotalsCaption), nameof(PacketsText), nameof(PacketsCaption), nameof(ErrorsText), nameof(ErrorsCaption),
            nameof(Utilization), nameof(UtilizationText), nameof(AdapterText), nameof(HasAdapter)
        }) OnPropertyChanged(name);
    }
}
