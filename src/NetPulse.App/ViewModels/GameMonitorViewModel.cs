using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.App.ViewModels;

public sealed class GameProfileItem
{
    public GameProfileItem(GameProfile profile) { Profile = profile; }
    public GameProfile Profile { get; }
    public string Id => Profile.Id;
    public string Display => $"{Profile.DisplayName}  ({StatusLabel(Profile.Status)})";
    public override string ToString() => Display;

    public static string StatusLabel(SupportStatus s) => s switch
    {
        SupportStatus.Supported => "Supported",
        SupportStatus.ReferenceOnly => "Reference-only",
        _ => "Not implemented"
    };
}

public sealed class ConnectionRow : ViewModelBase
{
    private string _state = "", _lastSeen = "", _measuredBy = "";
    private bool _isActive;

    public ConnectionRow(ConnectionView v) { Id = v.Id; Update(v); }

    public string Id { get; }
    public string Protocol { get; private set; } = "";
    public string Local { get; private set; } = "";
    public string Remote { get; private set; } = "";
    public string Scope { get; private set; } = "";
    public string Role { get; private set; } = "";
    public int Pid { get; private set; }
    public string FirstSeen { get; private set; } = "";
    public string Hint { get; private set; } = "";
    public bool HasRemote { get; private set; }
    public string State { get => _state; private set => SetProperty(ref _state, value); }
    public string LastSeen { get => _lastSeen; private set => SetProperty(ref _lastSeen, value); }
    public string MeasuredBy { get => _measuredBy; private set => SetProperty(ref _measuredBy, value); }
    public bool IsActive { get => _isActive; private set { if (SetProperty(ref _isActive, value)) OnPropertyChanged(nameof(Status)); } }
    public string Status => IsActive ? State : "Ended";

    public void Update(ConnectionView v)
    {
        Protocol = v.Protocol; Local = v.Local; Remote = v.Remote; Scope = v.Scope; Role = v.Role; Pid = v.Pid;
        FirstSeen = v.FirstSeenUtc.ToLocalTime().ToString("HH:mm:ss"); Hint = v.MeasureHint; HasRemote = v.HasRemote;
        State = v.State;
        LastSeen = v.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
        MeasuredBy = v.MeasuredBy ?? "";
        IsActive = v.IsActive;
    }
}

/// <summary>A row in either the "measured endpoints" or the "reference measurements" list.</summary>
public sealed class MeasurementRow : ViewModelBase
{
    private string _latency = "-", _variation = "-", _loss = "-", _status = "Waiting for the first probe...";

    public MeasurementRow(string id, string name, string method, string kind, string note)
    {
        Id = id; Name = name; Method = method; Kind = kind; Note = note;
    }

    public string Id { get; }
    public string Name { get; }
    public string Method { get; }
    public string Kind { get; }
    public string Note { get; }
    public string Latency { get => _latency; private set => SetProperty(ref _latency, value); }
    public string Variation { get => _variation; private set => SetProperty(ref _variation, value); }
    public string Loss { get => _loss; private set => SetProperty(ref _loss, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void Update(TargetMetrics m)
    {
        bool replied = m.ReplyCount > 0;
        Latency = replied && m.IsReachable ? $"{m.CurrentLatency:F1} ms" : "no reply";
        Variation = m.RecentSampleCount >= 2 && replied ? $"{m.Jitter:F1} ms" : "-";
        Loss = m.RecentSampleCount > 0 ? $"{m.PacketLossPercent:F0}% of {m.RecentSampleCount} probes / {m.RecentWindowSeconds:F0} s" : "-";

        Status = m.TotalProbes == 0 ? "Waiting for the first probe..."
            : !replied && m.TotalProbes >= 3 ? "No replies yet - the endpoint may filter this kind of probe (not necessarily offline)."
            : !m.IsReachable ? "Stopped answering."
            : m.IsLatencyElevated ? "Slower than this target's own normal."
            : "Answering.";
    }
}

public sealed class FindingItem
{
    public FindingItem(GameFinding f)
    {
        Title = f.Title;
        ConfidenceText = $"Confidence: {f.Confidence}";
        Observations = string.Join("\n", f.Observations.Select(o => "• " + o));
        NextTest = f.NextTest;
        Details = BuildDetails(f);
        Brush = f.Severity switch
        {
            FindingSeverity.Warning => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            FindingSeverity.Notice => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
        };
        Brush.Freeze();
    }

    public string Title { get; }
    public string ConfidenceText { get; }
    public string Observations { get; }
    public string NextTest { get; }
    public string Details { get; }
    public Brush Brush { get; }

    private static string BuildDetails(GameFinding f)
    {
        var sb = new StringBuilder();
        if (f.PossibleExplanations.Count > 0) sb.AppendLine("Possible explanations:").AppendLine(string.Join("\n", f.PossibleExplanations.Select(x => "  - " + x)));
        if (f.Evidence.Count > 0) sb.AppendLine("Supporting evidence:").AppendLine(string.Join("\n", f.Evidence.Select(x => "  - " + x)));
        if (f.Contradictions.Count > 0) sb.AppendLine("Measurements pointing the other way:").AppendLine(string.Join("\n", f.Contradictions.Select(x => "  - " + x)));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Presentation logic for the Game Monitor tab. All state comes from the shared <see cref="GameMonitorService"/>.</summary>
public sealed class GameMonitorViewModel : ViewModelBase
{
    private static readonly Brush RunningBrush = Frozen(Color.FromRgb(16, 185, 129));
    private static readonly Brush WaitingBrush = Frozen(Color.FromRgb(245, 158, 11));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly NetPulseHost _host;
    private readonly GameMonitorService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, ConnectionRow> _connectionRows = new();
    private readonly Dictionary<string, MeasurementRow> _measuredRows = new();
    private readonly Dictionary<string, MeasurementRow> _referenceRows = new();
    private int _refreshQueued;
    private GameProfileItem? _selectedProfile;
    private GameMonitorSnapshot _snapshot = new();
    private DateTime? _sessionStart;

    private string _processStatus = "Choose a game to begin.";
    private Brush _processBrush = System.Windows.Media.Brushes.Gray;
    private string _elapsed = "00:00:00";
    private string _endpointSummary = "";
    private string _connectionsEmptyText = "Choose a game, then press Start monitoring.";
    private string _actionMessage = "";
    private string _warningText = "";
    private string _sessionMessage = "";
    private string _profileSummary = "";
    private string _limitationsText = "";
    private string _statusPageText = "";
    private string? _statusPageUrl;
    private bool _isMonitoring;
    private ConnectionRow? _selectedConnection;
    private MeasurementRow? _selectedMeasured;
    private bool _isVisible;
    private string _findingsSignature = "";
    private string? _lastSavedId;

    /// <summary>Raised when the user asks to open a saved recording in History &amp; reports.</summary>
    public event EventHandler<string>? OpenRecordingRequested;

    public GameMonitorViewModel(NetPulseHost host, Dispatcher dispatcher)
    {
        _host = host;
        _monitor = host.GameMonitor;
        _dispatcher = dispatcher;

        Profiles = new ObservableCollection<GameProfileItem>(GameProfiles.All.Select(p => new GameProfileItem(p)));

        StartCommand = new RelayCommand(Start, () => !IsMonitoring && SelectedProfile != null);
        StopCommand = new RelayCommand(Stop, () => IsMonitoring);
        MarkLagCommand = new RelayCommand(MarkLag, () => IsMonitoring);
        MeasureIcmpCommand = new RelayCommand(() => Measure(EndpointMethod.Icmp), () => IsMonitoring && SelectedConnection != null);
        MeasureTcpCommand = new RelayCommand(() => Measure(EndpointMethod.TcpConnect), () => IsMonitoring && SelectedConnection != null);
        StopMeasuringCommand = new RelayCommand(StopMeasuring, () => SelectedMeasured != null);
        OpenStatusPageCommand = new RelayCommand(OpenStatusPage, () => !string.IsNullOrEmpty(_statusPageUrl));
        OpenSessionsFolderCommand = new RelayCommand(OpenSessionsFolder);
        OpenRecordingCommand = new RelayCommand(() => { if (_lastSavedId != null) OpenRecordingRequested?.Invoke(this, _lastSavedId); }, () => _lastSavedId != null && !IsMonitoring);
        _host.SessionSaved += (_, e) => { if (e.Session.GameId != null) _dispatcher.InvokeAsync(() => OnSessionSaved(e)); };

        _monitor.Updated += (_, _) => QueueRefresh();
        _host.Engine.MetricsUpdated += (_, _) => QueueRefresh();
        _host.Events.EventLogged += (_, e) => _dispatcher.InvokeAsync(() => OnEvent(e));

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed(), _dispatcher);
        _clock.Stop();

        // restore the previously selected game (starts light-weight process detection only)
        var last = Profiles.FirstOrDefault(p => p.Id == host.Settings.LastGameId);
        if (last != null) SelectedProfile = last;
    }

    public ObservableCollection<GameProfileItem> Profiles { get; }
    public ObservableCollection<ConnectionRow> Connections { get; } = new();
    public ObservableCollection<MeasurementRow> MeasuredEndpoints { get; } = new();
    public ObservableCollection<MeasurementRow> ReferenceTargets { get; } = new();
    public ObservableCollection<FindingItem> Findings { get; } = new();
    public ObservableCollection<ConnectionEvent> Timeline { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand MarkLagCommand { get; }
    public RelayCommand MeasureIcmpCommand { get; }
    public RelayCommand MeasureTcpCommand { get; }
    public RelayCommand StopMeasuringCommand { get; }
    public RelayCommand OpenStatusPageCommand { get; }
    public RelayCommand OpenSessionsFolderCommand { get; }
    public RelayCommand OpenRecordingCommand { get; }

    /// <summary>
    /// Set by the shell when the Game Monitor page is shown. While hidden only the cheap parts (process status for the
    /// Overview card) are refreshed; the connection table and findings are rebuilt when the page comes back.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value) && value) QueueRefresh(); }
    }

    /// <summary>Start recording when the game starts and save when it closes. Applied and saved immediately.</summary>
    public bool AutoRecord
    {
        get => _host.Settings.AutoRecordGame;
        set
        {
            if (_host.Settings.AutoRecordGame == value) return;
            _host.Settings.AutoRecordGame = value;
            _host.SaveSettings(out _);
            OnPropertyChanged();
            ActionMessage = value
                ? "Automatic recording is on: NetPulse starts recording when the game starts and saves the recording once it has been closed for 20 seconds."
                : "Automatic recording is off.";
        }
    }

    public string MarkLagShortcutText => _host.Settings.MarkLagHotkey == Core.Models.HotkeyGesture.Off
        ? "Tip: set a keyboard shortcut for Mark lag in Settings, so you can mark lag without leaving the game."
        : $"Shortcut: press {_host.Settings.MarkLagHotkey} in game to mark lag (works while the game has focus).";
    public void NotifyShortcutChanged() => OnPropertyChanged(nameof(MarkLagShortcutText));

    public GameProfileItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            if (IsMonitoring) { OnPropertyChanged(); return; } // cannot switch while recording; binding reverts
            _selectedProfile = value;
            OnPropertyChanged();

            _monitor.Select(value?.Id, out var message);
            _host.Settings.LastGameId = value?.Id;
            _host.SaveSettings(out _);
            ActionMessage = "";
            SessionMessage = "";
            RenderProfile(value?.Profile);
            QueueRefresh();
        }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set { if (SetProperty(ref _isMonitoring, value)) { OnPropertyChanged(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsIdle => !IsMonitoring;

    public string ProcessStatus { get => _processStatus; private set => SetProperty(ref _processStatus, value); }
    public Brush ProcessBrush { get => _processBrush; private set => SetProperty(ref _processBrush, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string EndpointSummary { get => _endpointSummary; private set => SetProperty(ref _endpointSummary, value); }
    public string ConnectionsEmptyText { get => _connectionsEmptyText; private set => SetProperty(ref _connectionsEmptyText, value); }
    public bool ShowConnectionsEmptyText => Connections.Count == 0;
    public string ActionMessage { get => _actionMessage; private set => SetProperty(ref _actionMessage, value); }
    public string WarningText { get => _warningText; private set { if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(_warningText);
    public string SessionMessage { get => _sessionMessage; private set => SetProperty(ref _sessionMessage, value); }
    public string ProfileSummary { get => _profileSummary; private set => SetProperty(ref _profileSummary, value); }
    public string LimitationsText { get => _limitationsText; private set => SetProperty(ref _limitationsText, value); }
    public string StatusPageText { get => _statusPageText; private set => SetProperty(ref _statusPageText, value); }
    public bool HasNoFindings => Findings.Count == 0;
    public string NoFindingsText => IsMonitoring
        ? "Nothing notable yet. Findings appear once enough recent measurements exist, and are rechecked every couple of seconds."
        : "Start monitoring to get findings.";
    public string MeasurementNote =>
        "Reference measurements (your router, DNS and public endpoints) show the health of your connection in general. They are not game-server ping. "
        + "In-game latency is not measured by NetPulse.";

    public ConnectionRow? SelectedConnection
    {
        get => _selectedConnection;
        set { if (SetProperty(ref _selectedConnection, value)) { OnPropertyChanged(nameof(SelectedConnectionHint)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string SelectedConnectionHint => SelectedConnection == null
        ? "Select a connection to see which measurement methods make sense for it."
        : SelectedConnection.Hint;

    public MeasurementRow? SelectedMeasured
    {
        get => _selectedMeasured;
        set { if (SetProperty(ref _selectedMeasured, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    // ------------------------------------------------------------------ actions

    private void Start()
    {
        Timeline.Clear();
        if (_monitor.Start(out var message))
        {
            IsMonitoring = true;
            _sessionStart = DateTime.UtcNow;
            _clock.Start();
            SessionMessage = "";
            ActionMessage = "Monitoring started. You can leave NetPulse running; it will pick up the game when it launches.";
        }
        else
        {
            ActionMessage = message;
        }
        QueueRefresh();
    }

    private void Stop()
    {
        var session = _monitor.Stop(out var error);
        IsMonitoring = false;
        _clock.Stop();
        _sessionStart = null;

        if (session == null) SessionMessage = "No session was being recorded.";
        // otherwise OnSessionSaved reports the result (it also covers recordings stopped automatically)
        ActionMessage = "";
        QueueRefresh();
    }

    private void OnSessionSaved(SessionSavedEventArgs e)
    {
        var session = e.Session;
        if (e.Error == null)
        {
            _lastSavedId = session.Id;
            SessionMessage = $"Recording saved: {session.Name}. {session.Samples.Count:N0} probes, {session.Connections.Count} connections, {session.Events.Count} events, "
                + $"{session.Gaps.Count} monitoring gaps, {session.LagMarkers.Count} lag markers. Open it in History & reports to see the graph and findings.";
        }
        else
        {
            _lastSavedId = null;
            SessionMessage = $"The session was recorded but could not be saved: {e.Error}";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void MarkLag()
    {
        ActionMessage = _monitor.MarkLag() ? "Lag marked at the current time." : "Start monitoring first.";
    }

    private void Measure(EndpointMethod method)
    {
        if (SelectedConnection == null) return;
        _monitor.MeasureEndpoint(SelectedConnection.Id, method, out var message);
        ActionMessage = message;
    }

    private void StopMeasuring()
    {
        if (SelectedMeasured == null) return;
        _monitor.StopMeasuring(SelectedMeasured.Id);
    }

    private void OpenStatusPage()
    {
        if (string.IsNullOrEmpty(_statusPageUrl)) return;
        try
        {
            // opens in the user's browser on explicit request; NetPulse itself does not contact this page
            Process.Start(new ProcessStartInfo(_statusPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionMessage = $"Could not open the status page: {ex.Message}";
        }
    }

    private void OpenSessionsFolder()
    {
        try
        {
            Directory.CreateDirectory(_host.Sessions.Directory);
            Process.Start(new ProcessStartInfo(_host.Sessions.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionMessage = $"Could not open the sessions folder: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ refresh (UI thread only)

    private void QueueRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshQueued, 1) == 1) return; // coalesce bursts
        _dispatcher.InvokeAsync(() =>
        {
            System.Threading.Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void RenderProfile(GameProfile? p)
    {
        if (p == null)
        {
            ProfileSummary = "";
            LimitationsText = "";
            StatusPageText = "";
            _statusPageUrl = null;
            return;
        }

        ProfileSummary = $"{p.DisplayName} - {GameProfileItem.StatusLabel(p.Status)}. {p.StatusExplanation}"
            + (p.ProcessNamesVerified ? "" : " Process names for this game have not been confirmed on a real installation, so detection may fail.");
        LimitationsText = string.Join("\n", p.Limitations.Select(l => "• " + l));
        _statusPageUrl = p.StatusPage.Url;
        StatusPageText = p.StatusPage.Url != null ? $"Official status: {p.StatusPage.Url}" : p.StatusPage.Note;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Refresh()
    {
        _snapshot = _monitor.Snapshot();
        var snap = _snapshot;

        // process status
        if (snap.Profile == null)
        {
            ProcessStatus = "Choose a game to begin.";
            ProcessBrush = System.Windows.Media.Brushes.Gray;
        }
        else if (snap.Process == null)
        {
            ProcessStatus = $"Checking whether {snap.Profile.DisplayName} is running...";
            ProcessBrush = System.Windows.Media.Brushes.Gray;
        }
        else
        {
            ProcessStatus = snap.Process.Message + (SelectedAdvanced && snap.Process.State == GameRunState.Running
                ? $"  [PIDs: {string.Join(", ", snap.Process.GameProcesses.Select(p => p.Pid))}; confidence {snap.Process.Confidence}]" : "");
            ProcessBrush = snap.Process.State switch
            {
                GameRunState.Running => RunningBrush,
                GameRunState.NotRunning => System.Windows.Media.Brushes.Gray,
                _ => WaitingBrush
            };
        }

        bool wasMonitoring = IsMonitoring;
        IsMonitoring = snap.IsMonitoring;
        if (!wasMonitoring && snap.IsMonitoring && _monitor.StartedAutomatically)
        {
            Timeline.Clear();
            SessionMessage = "";
            ActionMessage = $"{snap.Profile?.DisplayName} started, so recording started automatically. It is saved once the game has been closed for 20 seconds.";
            _clock.Start();
        }
        else if (wasMonitoring && !snap.IsMonitoring)
        {
            _clock.Stop();
        }

        if (!IsVisible)
        {
            UpdateElapsed();
            return; // the tables below are rebuilt when the page is shown again
        }

        // connections: update rows in place so selection survives
        var seen = new HashSet<string>();
        foreach (var v in snap.Connections)
        {
            seen.Add(v.Id);
            if (_connectionRows.TryGetValue(v.Id, out var row)) row.Update(v);
            else { row = new ConnectionRow(v); _connectionRows[v.Id] = row; Connections.Add(row); }
        }
        foreach (var gone in _connectionRows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Connections.Remove(_connectionRows[gone]);
            _connectionRows.Remove(gone);
        }
        OnPropertyChanged(nameof(ShowConnectionsEmptyText));

        EndpointSummary = snap.IsMonitoring
            ? $"{snap.DistinctRemoteEndpoints} remote endpoint(s) on the Internet, {snap.UdpSocketCount} UDP socket(s) in use (UDP remote peers are not visible to NetPulse)."
            : "";

        ConnectionsEmptyText = EmptyText(snap);

        // measured endpoints (game-process endpoints chosen by the user) and reference targets
        var measuredIds = snap.Measured.Select(m => m.TargetId).ToHashSet();
        var metrics = _host.Engine.GetCurrentMetrics();
        SyncRows(MeasuredEndpoints, _measuredRows, snap.Measured.Select(m => (m.TargetId, (Func<MeasurementRow>)(() =>
            new MeasurementRow(m.TargetId, m.Name, m.Method, "Endpoint observed on the game process - role unknown",
                m.Caveat + (m.ConnectionStillObserved ? "" : " (the game connection is no longer observed)"))))).ToList());
        foreach (var m in metrics)
        {
            if (_measuredRows.TryGetValue(m.TargetId, out var mr)) mr.Update(m);
        }

        var refs = metrics.Where(m => !m.IsGameEndpoint).ToList();
        SyncRows(ReferenceTargets, _referenceRows, refs.Select(m => (m.TargetId, (Func<MeasurementRow>)(() =>
            new MeasurementRow(m.TargetId, m.TargetName, m.Protocol.ToString().ToUpperInvariant(),
                m.IsGateway ? "Your router" : m.IsDnsResolver ? "DNS lookup" : "Public reference endpoint - not a game server", "")))).ToList());
        foreach (var m in refs)
        {
            if (_referenceRows.TryGetValue(m.TargetId, out var rr)) rr.Update(m);
        }

        // findings: rebuilt only when they change, so an expanded "details" section does not snap shut every second
        string signature = string.Join("\u001f", snap.Findings.Select(f => f.Title + "|" + f.Confidence + "|" + string.Join("|", f.Observations) + "|" + string.Join("|", f.Evidence)));
        if (signature != _findingsSignature)
        {
            _findingsSignature = signature;
            Findings.Clear();
            foreach (var f in snap.Findings) Findings.Add(new FindingItem(f));
        }
        OnPropertyChanged(nameof(HasNoFindings));
        OnPropertyChanged(nameof(NoFindingsText));

        WarningText = snap.Warnings.Count == 0 ? "" : "Some connection information is unavailable: " + string.Join(" ", snap.Warnings);
        UpdateElapsed();
    }

    private bool SelectedAdvanced => Application.Current?.MainWindow?.DataContext is MainViewModel vm && vm.IsAdvancedMode;

    private string EmptyText(GameMonitorSnapshot snap)
    {
        if (snap.Profile == null) return "Choose a game, then press Start monitoring.";
        if (!snap.IsMonitoring) return Connections.Count > 0 ? "" : "Press Start monitoring to record this game's connections.";
        return snap.Process?.State switch
        {
            GameRunState.Running => "The game is running, but no network sockets are visible for it yet.",
            GameRunState.LauncherOnly => $"Waiting for {snap.Profile.DisplayName} itself to start (only its launcher/helper is running).",
            GameRunState.Uncertain => "A look-alike process was found but is not confirmed as the game, so its connections are not tracked.",
            _ => $"Waiting for {snap.Profile.DisplayName} to start. Monitoring is already running; you can launch the game now."
        };
    }

    private static void SyncRows(ObservableCollection<MeasurementRow> list, Dictionary<string, MeasurementRow> index, List<(string Id, Func<MeasurementRow> Create)> wanted)
    {
        var ids = wanted.Select(w => w.Id).ToHashSet();
        foreach (var w in wanted)
        {
            if (index.ContainsKey(w.Id)) continue;
            var row = w.Create();
            index[w.Id] = row;
            list.Add(row);
        }
        foreach (var gone in index.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            list.Remove(index[gone]);
            index.Remove(gone);
        }
    }

    private void UpdateElapsed()
    {
        var start = _snapshot.StartedUtc ?? _sessionStart;
        Elapsed = start.HasValue && IsMonitoring ? (DateTime.UtcNow - start.Value).ToString(@"hh\:mm\:ss") : "00:00:00";
    }

    private void OnEvent(ConnectionEvent e)
    {
        if (!IsMonitoring) return;
        Timeline.Insert(0, e);
        while (Timeline.Count > 300) Timeline.RemoveAt(Timeline.Count - 1);
    }
}
