using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NetPulse.App.Controls;
using NetPulse.Core;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.App.ViewModels;

public sealed class SessionRow
{
    public SessionRow(SessionSummary s)
    {
        Id = s.Id;
        Name = s.Name;
        Game = string.IsNullOrEmpty(s.GameId) ? "Network only" : (s.GameDisplayName ?? Core.Games.GameProfiles.Find(s.GameId)?.DisplayName ?? s.GameId);
        StartedText = s.StartedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm");
        var end = s.EndedUtc;
        DurationText = end.HasValue ? FormatDuration(end.Value - s.StartedUtc) : "-";
        SamplesText = $"{s.SampleCount:N0} probes";
        EventsText = $"{s.EventCount} events";
        LagText = s.LagMarkerCount == 0 ? "" : $"{s.LagMarkerCount} lag mark{(s.LagMarkerCount == 1 ? "" : "s")}";
        IsComplete = s.IsComplete;
        Status = s.IsComplete ? "Saved" : "Interrupted";
    }

    public string Id { get; }
    public string Name { get; }
    public string Game { get; }
    public string StartedText { get; }
    public string DurationText { get; }
    public string SamplesText { get; }
    public string EventsText { get; }
    public string LagText { get; }
    public bool HasLag => LagText.Length > 0;
    public bool IsComplete { get; }
    public string Status { get; }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s" : $"{Math.Max(0, d.Seconds)} s";
}

public sealed class TargetStatRow
{
    public TargetStatRow(TargetStats t)
    {
        Name = t.Name;
        Kind = t.KindLabel;
        Method = t.Method;
        Probes = t.Probes.ToString("N0");
        Loss = $"{t.LossPercent:F1}%";
        Median = Ms(t.MedianMs);
        P95 = Ms(t.P95Ms);
        Max = Ms(t.MaxMs);
        Variation = Ms(t.VariationMs);
    }

    public string Name { get; }
    public string Kind { get; }
    public string Method { get; }
    public string Probes { get; }
    public string Loss { get; }
    public string Median { get; }
    public string P95 { get; }
    public string Max { get; }
    public string Variation { get; }
    private static string Ms(double? v) => v.HasValue ? $"{v.Value:F1} ms" : "-";
}

public sealed class LagRow
{
    public LagRow(LagCorrelation c)
    {
        TimeText = c.MarkerUtc.ToLocalTime().ToString("HH:mm:ss");
        Note = string.IsNullOrEmpty(c.Note) ? "Lag marked" : c.Note;
        var sb = new StringBuilder();
        var affected = c.Affected.ToList();
        if (affected.Count == 0 && c.NearbyEvents.Count == 0)
            sb.Append($"Nothing unusual was measured within {SessionAnalysis.LagWindowSeconds} s. The lag may have been on the game's side or on a path these targets do not share.");
        foreach (var a in affected)
            sb.AppendLine($"• {a.Name}: {(a.WindowMedianMs.HasValue ? $"{a.WindowMedianMs:F0} ms" : "no replies")} around this moment, normally {a.SessionMedianMs:F0} ms; {a.WindowFailures} of {a.WindowProbes} probes unanswered.");
        foreach (var e in c.NearbyEvents.Take(4))
            sb.AppendLine($"• {e.Timestamp.ToLocalTime():HH:mm:ss} {Lexicon.EventLabel(e.EventType)}: {e.Description}");
        Detail = sb.ToString().TrimEnd();
    }

    public string TimeText { get; }
    public string Note { get; }
    public string Detail { get; }
}

public sealed class LegendItem : ViewModelBase
{
    private bool _isOn = true;
    public LegendItem(string id, string name, Color color, Action changed, bool isOn = true)
    {
        _isOn = isOn;
        TargetId = id;
        Name = name;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
        _changed = changed;
    }

    private readonly Action _changed;
    public string TargetId { get; }
    public string Name { get; }
    public Brush Brush { get; }
    public bool IsOn { get => _isOn; set { if (SetProperty(ref _isOn, value)) _changed(); } }
}

/// <summary>History &amp; reports: browse saved recordings, see their graphs, statistics and lag-marker analysis, export a report.</summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly NetPulseHost _host;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _loadCts;
    private MonitoringSession? _session;
    private SessionStats? _stats;

    private SessionRow? _selected;
    private bool _isLoading;
    private string _message = "";
    private string _detailTitle = "";
    private string _detailSubtitle = "";
    private string _limitationsText = "";
    private string _connectionsText = "";
    private ChartData? _chart;
    private int _modeIndex;
    private string _sessionsFolderText = "";

    public HistoryViewModel(NetPulseHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _sessionsFolderText = host.Sessions.Directory;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        ExportReportCommand = new RelayCommand(ExportReport, () => _session != null);
        ExportSamplesCommand = new RelayCommand(ExportSamples, () => _session != null);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        DeleteCommand = new RelayCommand(Delete, () => _session != null);
        Patterns = new PatternsViewModel(host, dispatcher);
    }

    /// <summary>Where results of exports and deletions are shown (the page's own message line is out of sight when scrolled down).</summary>
    public ToastViewModel? Toast { get; set; }

    /// <summary>"When is my connection worst?" across all recordings.</summary>
    public PatternsViewModel Patterns { get; }

    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public ObservableCollection<TargetStatRow> TargetStats { get; } = new();
    public ObservableCollection<LagRow> LagMarkers { get; } = new();
    public ObservableCollection<FindingItem> Findings { get; } = new();
    public ObservableCollection<ConnectionEvent> Events { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<SummaryCard> Cards { get; } = new();
    public ObservableCollection<SessionRow> CompareChoices { get; } = new();
    public ObservableCollection<CompareRow> CompareRows { get; } = new();

    private SessionRow? _compareWith;
    private string _compareSummary = "";
    private CancellationTokenSource? _compareCts;
    /// <summary>The other recording this one is compared with (the reference, "before").</summary>
    public SessionRow? CompareWith
    {
        get => _compareWith;
        set { if (SetProperty(ref _compareWith, value)) { OnPropertyChanged(nameof(HasCompareWith)); _ = LoadComparisonAsync(value?.Id); } }
    }
    public bool HasCompareWith => _compareWith != null;
    public string CompareSummary { get => _compareSummary; private set => SetProperty(ref _compareSummary, value); }
    public bool HasComparison => CompareRows.Count > 0;
    public bool CanCompare => CompareChoices.Count > 0;
    public string CompareColumnA => _compareWith == null ? "Before" : "Other";
    public RelayCommand ClearCompareCommand => _clearCompare ??= new RelayCommand(() => CompareWith = null, () => _compareWith != null);
    private RelayCommand? _clearCompare;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand ExportSamplesCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public IReadOnlyList<string> ModeLabels { get; } = new[] { "Latency", "Variation", "Probe loss", "Network activity" };

    // ---- "Network activity" view of a recording (traffic on this PC, recorded since 0.13)
    public const int ActivityMode = 3;
    public bool IsActivityMode => ModeIndex == ActivityMode;
    public bool IsLatencyMode => ModeIndex != ActivityMode;
    private IReadOnlyList<ActivityPoint> _trafficPoints = Array.Empty<ActivityPoint>();
    private IReadOnlyList<PingPoint> _trafficPings = Array.Empty<PingPoint>();
    private IReadOnlyList<DateTime> _trafficMarkers = Array.Empty<DateTime>();
    private double _trafficWindow = 60;
    public IReadOnlyList<ActivityPoint> TrafficPoints { get => _trafficPoints; private set => SetProperty(ref _trafficPoints, value); }
    public IReadOnlyList<PingPoint> TrafficPings { get => _trafficPings; private set => SetProperty(ref _trafficPings, value); }
    public IReadOnlyList<DateTime> TrafficMarkers { get => _trafficMarkers; private set => SetProperty(ref _trafficMarkers, value); }
    public double TrafficWindowSeconds { get => _trafficWindow; private set => SetProperty(ref _trafficWindow, value); }
    public bool HasTraffic => _trafficPoints.Count >= 2;
    public string TrafficSummary { get; private set; } = "";

    private void BuildTraffic(MonitoringSession s)
    {
        TrafficPoints = s.Throughput.Select(t => new ActivityPoint(t.T, t.Rx, t.Tx, 0, 0)).ToList();
        var refs = s.Targets.Where(t => t.Kind == MeasurementKind.ReferenceEndpoint && t.Protocol == NetworkProtocol.Icmp).Select(t => t.Id).ToHashSet();
        TrafficPings = s.Samples.Where(x => x.Ok && x.RttMs.HasValue && refs.Contains(x.TargetId))
            .GroupBy(x => new DateTime(x.T.Ticks - x.T.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new PingPoint(g.Key.AddMilliseconds(500), NetPulse.Core.Statistics.StatisticalCalculator.CalculateMedian(g.Select(x => x.RttMs!.Value).ToList())))
            .ToList();
        TrafficMarkers = s.LagMarkers.Select(m => m.T).ToList();
        var first = s.Throughput.FirstOrDefault()?.T ?? s.StartedUtc;
        var last = s.Throughput.LastOrDefault()?.T ?? s.EndedUtc ?? s.StartedUtc;
        TrafficWindowSeconds = Math.Max(30, (last - first).TotalSeconds);
        if (s.Throughput.Count < 2)
            TrafficSummary = "This recording has no traffic data: recordings keep this PC's network activity since NetPulse 0.13.";
        else
        {
            double peakRx = s.Throughput.Max(t => t.Rx), peakTx = s.Throughput.Max(t => t.Tx);
            double avgRx = s.Throughput.Average(t => t.Rx), avgTx = s.Throughput.Average(t => t.Tx);
            TrafficSummary = $"Traffic on this PC during the recording: download peak {AdapterActivityMonitor.FormatBps(peakRx)}, average {AdapterActivityMonitor.FormatBps(avgRx)}; " +
                             $"upload peak {AdapterActivityMonitor.FormatBps(peakTx)}, average {AdapterActivityMonitor.FormatBps(avgTx)}. " +
                             "The pink line is the reference ping and amber dashes are the moments you marked as laggy: lag that lines up with a traffic peak was probably caused by it.";
        }
        OnPropertyChanged(nameof(HasTraffic));
        OnPropertyChanged(nameof(TrafficSummary));
    }
    public string SessionsFolder => _sessionsFolderText;
    private string _storageText = "";
    /// <summary>"3 recordings, 4.2 MB on this PC" plus the retention rule.</summary>
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    private string _versionNote = "";
    /// <summary>Explains that stored findings come from the version that recorded the session.</summary>
    public string VersionNote { get => _versionNote; private set { if (SetProperty(ref _versionNote, value)) OnPropertyChanged(nameof(HasVersionNote)); } }
    public bool HasVersionNote => _versionNote.Length > 0;

    public SessionRow? SelectedSession
    {
        get => _selected;
        set
        {
            // while the list is being rebuilt the table reports "nothing selected"; that is not the user's choice
            if (_rebuildingList) return;
            if (!SetProperty(ref _selected, value)) return;
            if (value?.Id != _session?.Id || value == null) _ = LoadDetailAsync(value?.Id);
        }
    }

    private bool _rebuildingList;
    private int _refreshVersion;

    public bool HasSessions => Sessions.Count > 0;
    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasDetail => _session != null;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }
    public string DetailSubtitle { get => _detailSubtitle; private set => SetProperty(ref _detailSubtitle, value); }
    public string LimitationsText { get => _limitationsText; private set => SetProperty(ref _limitationsText, value); }
    public string ConnectionsText { get => _connectionsText; private set => SetProperty(ref _connectionsText, value); }
    public ChartData? Chart { get => _chart; private set => SetProperty(ref _chart, value); }
    public IReadOnlyList<string> ChartTargetOrder => _session?.Targets.Select(t => t.Id).ToList() ?? new List<string>();
    public bool HasNoLagMarkers => LagMarkers.Count == 0;
    public bool HasNoFindings => Findings.Count == 0;

    public string ChartCaption => ModeIndex switch
    {
        0 => "Slowest reply in each time slice, so short spikes stay visible. Amber dashes mark where you pressed Mark lag; grey bands are times NetPulse was not measuring; red bands are connection losses.",
        1 => "How much consecutive replies differed in each time slice. Steady lines are good: big swings feel like stutter even when the average ping is fine.",
        2 => "Share of test messages with no reply in each time slice. Short bursts on one target can just be that server ignoring pings.",
        _ => "Download and upload of this PC during the recording, with the reference ping on the right axis. Other devices at home are not included."
    };

    public int ModeIndex
    {
        get => _modeIndex;
        set { if (SetProperty(ref _modeIndex, value)) { OnPropertyChanged(nameof(ChartCaption)); OnPropertyChanged(nameof(IsActivityMode)); OnPropertyChanged(nameof(IsLatencyMode)); RebuildChart(); } }
    }

    // ------------------------------------------------------------------ loading

    public async Task RefreshAsync()
    {
        int version = ++_refreshVersion;
        IsLoading = true;
        try
        {
            var list = await Task.Run(() => _host.Sessions.List());
            if (version != _refreshVersion) return; // a newer refresh started meanwhile; it will apply its own result

            string? keep = _pendingOpenId ?? _selected?.Id;
            _rebuildingList = true;
            try
            {
                Sessions.Clear();
                foreach (var s in list) Sessions.Add(new SessionRow(s));
            }
            finally
            {
                _rebuildingList = false;
            }
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));

            long bytes = list.Sum(s => s.SizeBytes);
            int days = _host.Settings.RecordingRetentionDays;
            StorageText = $"{list.Count} recording{(list.Count == 1 ? "" : "s")}, {bytes / 1048576.0:0.#} MB on this PC. "
                + (days > 0 ? $"Recordings older than {days} days are deleted automatically (Settings)." : "Recordings are kept until you delete them (Settings can delete old ones automatically).");

            _pendingOpenId = null;
            var again = Sessions.FirstOrDefault(r => r.Id == keep);
            _selected = again;
            OnPropertyChanged(nameof(SelectedSession)); // re-highlights the row in the rebuilt table
            if (again == null) { if (_session != null) ClearDetail(); }
            else if (again.Id != _session?.Id) _ = LoadDetailAsync(again.Id);
        }
        catch (Exception ex)
        {
            Message = $"Could not read the recordings folder: {ex.Message}";
        }
        finally
        {
            if (version == _refreshVersion) IsLoading = false;
        }
    }

    private string? _pendingOpenId;

    /// <summary>Shows the recording with this id (used after a recording is saved elsewhere in the app).</summary>
    public Task OpenAsync(string id)
    {
        _pendingOpenId = id;
        return RefreshAsync();
    }

    private async Task LoadDetailAsync(string? id)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        if (id == null) { ClearDetail(); return; }

        IsLoading = true;
        Message = "";
        try
        {
            var (session, stats) = await Task.Run(() =>
            {
                var s = _host.Sessions.Load(id);
                return (s, s == null ? null : SessionAnalysis.Compute(s));
            });

            if (cts.IsCancellationRequested) return;
            if (session == null || stats == null) { ClearDetail(); Message = "That recording could not be found. It may have been moved or deleted."; return; }

            _session = session;
            _stats = stats;
            RenderDetail(session, stats);
        }
        catch (Exception ex)
        {
            ClearDetail();
            Message = $"That recording could not be opened: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
    }

    private async Task LoadComparisonAsync(string? otherId)
    {
        _compareCts?.Cancel();
        var cts = _compareCts = new CancellationTokenSource();
        CompareRows.Clear();
        CompareSummary = "";
        if (otherId == null || _stats == null || _session == null) { NotifyCompare(); return; }

        var current = _stats;
        string currentName = _session.Name;
        try
        {
            var other = await Task.Run<(MonitoringSession Session, SessionStats Stats)?>(() =>
            {
                var s = _host.Sessions.Load(otherId);
                return s == null ? null : (s, SessionAnalysis.Compute(s));
            });
            if (cts.IsCancellationRequested) return;
            if (other == null) { CompareSummary = "That recording could not be opened."; NotifyCompare(); return; }

            var result = SessionComparison.Compare(other.Value.Stats, current);
            foreach (var row in result.Targets) CompareRows.Add(new CompareRow(row));
            CompareSummary = result.Summary.Replace("Compared with the first recording, the second had", $"Compared with \"{other.Value.Session.Name}\", this recording had");
            if (result.OnlyInA.Count + result.OnlyInB.Count > 0)
                CompareSummary += $" Not compared (measured in only one of them): {string.Join(", ", result.OnlyInA.Concat(result.OnlyInB).Distinct())}.";
        }
        catch (Exception ex)
        {
            CompareSummary = $"The comparison could not be made: {ex.Message}";
        }
        NotifyCompare();
    }

    private void NotifyCompare()
    {
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(CompareColumnA));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResetCompareChoices()
    {
        _compareWith = null;
        OnPropertyChanged(nameof(CompareWith));
        OnPropertyChanged(nameof(HasCompareWith));
        CompareRows.Clear();
        CompareSummary = "";
        CompareChoices.Clear();
        if (_session != null)
            foreach (var r in Sessions.Where(r => r.Id != _session.Id)) CompareChoices.Add(r);
        OnPropertyChanged(nameof(CanCompare));
        NotifyCompare();
    }

    private void ClearDetail()
    {
        _session = null;
        _stats = null;
        TargetStats.Clear(); LagMarkers.Clear(); Findings.Clear(); Events.Clear(); Legend.Clear(); Cards.Clear();
        Chart = null;
        DetailTitle = DetailSubtitle = LimitationsText = ConnectionsText = VersionNote = "";
        ResetCompareChoices();
        NotifyDetail();
    }

    private void NotifyDetail()
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasNoLagMarkers));
        OnPropertyChanged(nameof(HasNoFindings));
        OnPropertyChanged(nameof(ChartTargetOrder));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RenderDetail(MonitoringSession s, SessionStats stats)
    {
        DetailTitle = s.Name;
        string game = string.IsNullOrEmpty(s.GameDisplayName) ? "Network recording" : s.GameDisplayName;
        DetailSubtitle = $"{game} • {s.StartedUtc.ToLocalTime():ddd d MMM yyyy, HH:mm} • {SessionRow.FormatDuration(stats.Duration)}"
            + (s.IsComplete ? "" : " • recording was interrupted, showing data up to the last checkpoint");

        Cards.Clear();
        Cards.Add(new SummaryCard("Length", SessionRow.FormatDuration(stats.Duration), s.IsComplete ? "Saved normally" : "Interrupted"));
        Cards.Add(new SummaryCard("Probes", stats.ProbeCount.ToString("N0"), $"{s.Targets.Count} targets"));
        Cards.Add(new SummaryCard("Events", s.Events.Count.ToString(), "see the list below"));
        Cards.Add(new SummaryCard("Lag marked", s.LagMarkers.Count.ToString(), s.LagMarkers.Count == 0 ? "none pressed" : "moments you flagged"));
        Cards.Add(new SummaryCard("Paused", stats.GapSeconds > 0 ? $"{stats.GapSeconds:F0} s" : "none", "NetPulse not measuring"));
        Cards.Add(new SummaryCard("Connection lost", stats.DisconnectSeconds > 0 ? $"{stats.DisconnectSeconds:F0} s" : "none", "no probe answered"));

        TargetStats.Clear();
        foreach (var t in stats.Targets) TargetStats.Add(new TargetStatRow(t));

        LagMarkers.Clear();
        foreach (var c in stats.LagCorrelations) LagMarkers.Add(new LagRow(c));

        Findings.Clear();
        foreach (var f in s.Findings) Findings.Add(new FindingItem(f));
        VersionNote = s.Findings.Count == 0 ? ""
            : s.AppVersion == null ? "These findings were worked out by an older NetPulse when the recording was saved. Later versions may judge the same measurements differently; the graph and statistics above are always recalculated."
            : s.AppVersion != Core.AppInfo.Version ? $"These findings were worked out by NetPulse {s.AppVersion} when the recording was saved. The graph and statistics above are recalculated by this version ({Core.AppInfo.Version})."
            : "";

        Events.Clear();
        foreach (var e in s.Events.OrderByDescending(e => e.Timestamp).Take(200)) Events.Add(e);

        LimitationsText = s.Limitations.Count == 0 ? "" : string.Join("\n", s.Limitations.Select(l => "• " + l));
        ConnectionsText = string.IsNullOrEmpty(s.GameDisplayName) ? "" :
            $"{s.GameProcesses.Count} game process(es) and {s.Connections.Count} sockets were recorded. Their role is unknown; none is assumed to be the match server.";

        Legend.Clear();
        var order = s.Targets.Select(t => t.Id).ToList();
        foreach (var t in s.Targets)
            // Plain pings are comparable with each other; an HTTPS handshake or DNS lookup includes extra work, so they start switched off
            Legend.Add(new LegendItem(t.Id, t.Name, SessionChart.ColorFor(t.Id, order), RebuildChart, isOn: t.Protocol == NetworkProtocol.Icmp));

        ResetCompareChoices();
        NotifyDetail();
        RebuildChart();
    }

    private void RebuildChart()
    {
        if (_session == null) { Chart = null; return; }
        var on = Legend.Where(l => l.IsOn).Select(l => l.TargetId).ToHashSet();
        Chart = SessionChartBuilder.Build(_session, (ChartMode)Math.Clamp(ModeIndex, 0, 2), on);
        BuildTraffic(_session);
    }

    // ------------------------------------------------------------------ actions

    private void ExportReport()
    {
        if (_session == null || _stats == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown report (*.md)|*.md",
            FileName = $"NetPulse_Report_{_session.StartedUtc.ToLocalTime():yyyyMMdd_HHmm}.md"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, SessionReportBuilder.BuildMarkdown(_session, _stats), Encoding.UTF8);
            if (Toast != null) Toast.Saved("Report", dlg.FileName); else Message = $"Report saved: {dlg.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Toast != null) Toast.Error($"The report could not be saved. {ToastViewModel.Reason(ex)}"); else Message = $"The report could not be saved: {ex.Message}";
        }
    }

    private void ExportSamples()
    {
        if (_session == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"NetPulse_Probes_{_session.StartedUtc.ToLocalTime():yyyyMMdd_HHmm}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, SessionReportBuilder.BuildSamplesCsv(_session), Encoding.UTF8);
            if (Toast != null) Toast.Saved("Probe data", dlg.FileName); else Message = $"Probe data saved: {dlg.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Toast != null) Toast.Error($"The probe data could not be saved. {ToastViewModel.Reason(ex)}"); else Message = $"The data could not be saved: {ex.Message}";
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_host.Sessions.Directory);
            Process.Start(new ProcessStartInfo(_host.Sessions.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Message = $"Could not open the folder: {ex.Message}";
        }
    }

    private void Delete()
    {
        if (_session == null) return;
        var answer = MessageBox.Show(
            $"Delete the recording \"{_session.Name}\"?\n\nThis removes the file from this PC and cannot be undone.",
            "Delete recording", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            string id = _session.Id;
            string name = _session.Name;
            _host.Sessions.Delete(id);
            Message = "Recording deleted.";
            Toast?.Show($"Deleted \"{name}\".");
            _selected = null;
            OnPropertyChanged(nameof(SelectedSession));
            ClearDetail();
            _ = RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"The recording could not be deleted: {ex.Message}";
        }
    }
}

public sealed record SummaryCard(string Label, string Value, string Caption);

/// <summary>One target in a two-recording comparison, formatted for the table.</summary>
public sealed class CompareRow
{
    public CompareRow(TargetComparison c)
    {
        Name = c.Name;
        Kind = c.KindLabel;
        MedianA = Ms(c.MedianA); MedianB = Ms(c.MedianB); MedianChange = Delta(c.MedianDelta, SessionComparison.MedianNoiseMs, "ms");
        VariationA = Ms(c.VariationA); VariationB = Ms(c.VariationB); VariationChange = Delta(c.VariationDelta, SessionComparison.VariationNoiseMs, "ms");
        LossA = $"{c.LossA:F1}%"; LossB = $"{c.LossB:F1}%"; LossChange = Delta(c.LossDelta, SessionComparison.LossNoisePercent, "pts");
        // lower is better for all three numbers
        double score = (c.MedianDelta ?? 0) / SessionComparison.MedianNoiseMs + (c.VariationDelta ?? 0) / SessionComparison.VariationNoiseMs + c.LossDelta / SessionComparison.LossNoisePercent;
        Verdict = Math.Abs(score) < 1 ? "About the same" : score < 0 ? "Better" : "Worse";
    }

    public string Name { get; }
    public string Kind { get; }
    public string MedianA { get; } public string MedianB { get; } public string MedianChange { get; }
    public string VariationA { get; } public string VariationB { get; } public string VariationChange { get; }
    public string LossA { get; } public string LossB { get; } public string LossChange { get; }
    public string Verdict { get; }

    private static string Ms(double? v) => v.HasValue ? $"{v.Value:F1} ms" : "-";
    private static string Delta(double? d, double noise, string unit) =>
        !d.HasValue ? "-" : Math.Abs(d.Value) < noise ? "same" : $"{(d.Value > 0 ? "+" : "")}{d.Value:F1} {unit}";
}
