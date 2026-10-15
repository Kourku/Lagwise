using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NetPulse.Core;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;
using NetPulse.Core.Services;

namespace NetPulse.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly NetworkDiscoveryService _discoveryService;
    private readonly PingService _pingService;
    private readonly TcpProbeService _tcpService;
    private readonly HttpProbeService _httpService;
    private readonly DnsProbeService _dnsService;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly EventLogService _eventLogService;
    private readonly StorageService _storageService;
    private readonly WindowsIntegrationService? _winIntegrationService;
    private readonly TracerouteService _tracerouteService;
    private readonly MtrService _mtrService;
    private readonly RouteAnalysisService _routeAnalysisService;
    private readonly BufferbloatService _bufferbloatService;
    private readonly SpeedTestService _speedTestService;
    private readonly MeasurementEngine _engine;

    // View Navigation
    private string _currentTab = "Dashboard";
    private bool _isBeginnerMode = true;
    private CancellationTokenSource? _bufferbloatCts;
    private CancellationTokenSource? _speedTestCts;
    private CancellationTokenSource? _compareCts;
    private bool _isComparingPaths;

    // Diagnostic state
    private string _statusHeadline = "Initializing connection diagnostics...";
    private string _statusCategory = "Analyzing";
    private string _likelyCause = "Gathering initial network packets across local and Internet hops.";
    private string _recommendedAction = "Monitoring active.";
    private static readonly Brush AnalyzingBrush = Frozen(Color.FromRgb(59, 130, 246));
    private static readonly Brush CriticalBrush = Frozen(Color.FromRgb(239, 68, 68));
    private static readonly Brush DegradedBrush = Frozen(Color.FromRgb(245, 158, 11));
    private static readonly Brush HealthyBrush = Frozen(Color.FromRgb(16, 185, 129));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private Brush _statusBrush = AnalyzingBrush;
    private string _evidenceSummary = string.Empty;

    // Quick stats
    private double _quickGatewayLatency;
    private double _quickInternetLatency;
    private double _quickJitter;
    private double _quickPacketLoss;
    private double _quickDnsLatency;

    // Network Interface
    private NetworkInterfaceInfo _activeNic = new();

    // MTR / Route Analysis
    private string _mtrDestination = "1.1.1.1";
    private bool _isMtrRunning;
    private string _mtrStatusText = "Ready to trace route path.";
    private string _pathCompareDestA = "1.1.1.1";
    private string _pathCompareDestB = "8.8.8.8";
    private string _pathCompareSummary = string.Empty;

    // Bufferbloat
    private bool _isBufferbloatRunning;
    private int _bufferbloatProgress;
    private string _bufferbloatStatus = "Ready when you are.";
    private BufferbloatResult? _latestBufferbloatResult;

    // Speed Test
    private bool _isSpeedTestRunning;
    private int _speedTestProgress;
    private double _currentSpeedTestMbps;
    private string _speedTestStatus = "Ready when you are.";
    private SpeedTestResult? _latestSpeedTestResult;

    public ObservableCollection<TargetMetricsViewModel> Targets { get; } = new();
    public ObservableCollection<ConnectionEvent> Events { get; } = new();
    /// <summary>The timeline as shown: all events, or only warnings and problems.</summary>
    public System.ComponentModel.ICollectionView EventsView { get; }
    private bool _showProblemsOnly;
    public bool ShowProblemsOnly
    {
        get => _showProblemsOnly;
        set { if (SetProperty(ref _showProblemsOnly, value)) { EventsView.Refresh(); OnPropertyChanged(nameof(HasNoEvents)); OnPropertyChanged(nameof(NoEventsText)); } }
    }
    public bool HasNoEvents => EventsView.IsEmpty;
    public string NoEventsText => ShowProblemsOnly && Events.Count > 0
        ? "No warnings or problems. Switch the filter off to see informational events too."
        : "Spikes, probe loss, router problems, route changes, game starts and your lag markers will appear here.";
    public ObservableCollection<RouteHop> MtrHops { get; } = new();
    public ObservableCollection<string> RouteAnomalies { get; } = new();

    // Commands
    public RelayCommand SwitchTabCommand { get; }
    public RelayCommand SetModeCommand { get; }
    public RelayCommand CancelBufferbloatCommand { get; }
    public RelayCommand CancelSpeedTestCommand { get; }
    public RelayCommand StartMtrCommand { get; }
    public RelayCommand StopMtrCommand { get; }
    public RelayCommand ComparePathsCommand { get; }
    public RelayCommand RunBufferbloatCommand { get; }
    public RelayCommand RunSpeedTestCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand ClearEventsCommand { get; }

    private readonly NetPulseHost _host;
    public GameMonitorViewModel GameMonitor { get; }
    /// <summary>Network activity page: adapter throughput, like Task Manager.</summary>
    public ActivityViewModel Activity { get; }
    /// <summary>Short results at the bottom of the window ("Report saved", "Could not save ...").</summary>
    public ToastViewModel Toast { get; } = new();
    public SettingsViewModel Settings { get; }
    public HistoryViewModel History { get; }
    public AppearanceViewModel Appearance { get; }

    public MainViewModel(NetPulseHost host)
    {
        _host = host;
        EventsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = o => !_showProblemsOnly || (o is ConnectionEvent e && e.Severity != "Info");
        Events.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasNoEvents)); OnPropertyChanged(nameof(NoEventsText)); };
        _discoveryService = host.Discovery;
        _pingService = host.Ping;
        _tcpService = host.Tcp;
        _httpService = host.Http;
        _dnsService = host.Dns;
        _diagnosticEngine = host.Diagnostics;
        _eventLogService = host.Events;
        _storageService = host.Storage;
        _winIntegrationService = host.WindowsIntegration;
        _tracerouteService = host.Traceroute;
        _mtrService = host.Mtr;
        _routeAnalysisService = host.RouteAnalysis;
        _bufferbloatService = host.Bufferbloat;
        _speedTestService = host.SpeedTest;
        _engine = host.Engine;
        _isBeginnerMode = host.Settings.IsBeginnerMode;
        _latestBufferbloatResult = host.LastBufferbloat; // the last test survives restarts

        // Bind engine events
        _engine.MetricsUpdated += (s, metrics) => Application.Current.Dispatcher.InvokeAsync(() => UpdateMetrics(metrics));
        _engine.DiagnosisUpdated += (s, diag) => Application.Current.Dispatcher.InvokeAsync(() => UpdateDiagnosis(diag));
        _engine.NetworkInterfaceUpdated += (s, nic) => Application.Current.Dispatcher.InvokeAsync(() => ActiveNic = nic);
        _eventLogService.EventLogged += (s, evt) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Events.Insert(0, evt);
            if (Events.Count > 300) Events.RemoveAt(Events.Count - 1);
            UpdateLatestEvent();
        });

        _mtrService.HopsUpdated += (s, hops) => Application.Current.Dispatcher.InvokeAsync(() => UpdateMtrHops(hops));
        _mtrService.RouteChanged += (s, chg) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _eventLogService.LogEvent("RouteChange", "Warning", chg.ChangeDescription, chg.Destination, chg.CurrentLatencyMs);
        });

        // Initialize Commands
        SwitchTabCommand = new RelayCommand(p => CurrentTab = p?.ToString() ?? "Dashboard");
        SetModeCommand = new RelayCommand(p => IsBeginnerMode = !string.Equals(p?.ToString(), "Advanced", StringComparison.OrdinalIgnoreCase));
        StartMtrCommand = new RelayCommand(async () => await StartMtrAsync(), () => !IsMtrRunning);
        StopMtrCommand = new RelayCommand(() => StopMtr(), () => IsMtrRunning);
        ComparePathsCommand = new RelayCommand(async () => await ComparePathsAsync(), () => !IsComparingPaths);
        RunBufferbloatCommand = new RelayCommand(async () => await RunBufferbloatAsync(), () => !IsBufferbloatRunning);
        RunSpeedTestCommand = new RelayCommand(async () => await RunSpeedTestAsync(), () => !IsSpeedTestRunning);
        CancelBufferbloatCommand = new RelayCommand(() => _bufferbloatCts?.Cancel(), () => IsBufferbloatRunning);
        CancelSpeedTestCommand = new RelayCommand(() => _speedTestCts?.Cancel(), () => IsSpeedTestRunning);
        ExportCsvCommand = new RelayCommand(async () => await ExportCsvAsync());
        ExportJsonCommand = new RelayCommand(async () => await ExportJsonAsync());
        ExportReportCommand = new RelayCommand(async () => await ExportReportAsync());
        ClearEventsCommand = new RelayCommand(() =>
        {
            _eventLogService.Clear();
            Events.Clear();
        });

        // Start measurement engine
        InitializeSessionCommands();
        GameMonitor = new GameMonitorViewModel(host, Application.Current.Dispatcher);
        History = new HistoryViewModel(host, Application.Current.Dispatcher) { Toast = Toast };
        Activity = new ActivityViewModel(host, Application.Current.Dispatcher, () => _activeNic?.Id);
        // the overlay's traffic line follows the adapter Windows uses for the internet, once a second, only while shown
        host.Activity.Sampled += (_, _) =>
        {
            if (!_host.Settings.OverlayVisible || !_host.Settings.OverlayShowActivity) return;
            var a = _host.InternetAdapterActivity();
            if (a?.Latest is not { } p) return;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                OverlayDown = AdapterActivityMonitor.FormatBps(p.RxBps);
                OverlayUp = AdapterActivityMonitor.FormatBps(p.TxBps);
            });
        };
        Settings = new SettingsViewModel(host);
        Appearance = new AppearanceViewModel(host);
        Appearance.Applied += (_, _) => OnPropertyChanged(nameof(ShowOverlayTraffic));
        // brushes taken from the palette in code (status tints, grade ring) are re-read after a theme switch
        NetPulse.App.Themes.ThemeManager.Changed += (_, _) =>
        {
            if (_lastDiagnosis != null) UpdateDiagnosis(_lastDiagnosis);
            OnPropertyChanged(nameof(GradeBrush));
            Appearance.Sync();
        };
        Settings.Applied += (_, _) =>
        {
            OnPropertyChanged(nameof(ProbeIntervalText));
            OnPropertyChanged(nameof(MarkLagKeyText));
            PruneRemovedTargets();
        };
        GameMonitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameMonitorViewModel.ProcessStatus) or nameof(GameMonitorViewModel.IsMonitoring) or nameof(GameMonitorViewModel.SelectedProfile)) UpdateGameCard();
        };
        MtrHops.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoHops));
        GameMonitor.OpenRecordingRequested += (_, id) => OpenRecording(id);
        _host.SessionSaved += (_, e) => Application.Current.Dispatcher.InvokeAsync(() => OnSessionSaved(e));
        UpdateGameCard();

        // live graph on the Overview: rebuilt every few seconds, only while the Overview is on screen
        _liveTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(3), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { if (CurrentTab == "Dashboard" && Application.Current.MainWindow?.IsVisible == true) RefreshLiveChart(); }, Application.Current.Dispatcher);
        LiveLegend.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LiveChartOrder));
        StartProbeLog();

        // Start the shared engine (idempotent: tray and window share one host)
        _host.Start();
    }

    #region Properties

    public string CurrentTab
    {
        get => _currentTab;
        set
        {
            if (!SetProperty(ref _currentTab, value)) return;
            if (value == "History") _ = History.RefreshAsync();   // pick up recordings saved since the tab was last open
            GameMonitor.IsVisible = value == "GameMonitor";
            Activity.IsVisible = value == "Activity";
            if (value == "Dashboard") RefreshLiveChart();
            if (value == "ProbeLog") RebuildProbeLog();
        }
    }

    public bool IsBeginnerMode
    {
        get => _isBeginnerMode;
        set
        {
            if (SetProperty(ref _isBeginnerMode, value))
            {
                OnPropertyChanged(nameof(IsAdvancedMode));
                OnPropertyChanged(nameof(ModeSummary));
                // a page that only exists in Advanced cannot stay open in Beginner
                if (value && AdvancedOnlyTabs.Contains(CurrentTab)) CurrentTab = "Dashboard";
                _host.Settings.ModeChosen = true;
                OnPropertyChanged(nameof(ShowModeChooser));
                OnPropertyChanged(nameof(TipLatency));
                OnPropertyChanged(nameof(TipVariation));
                OnPropertyChanged(nameof(TipLoss));
                OnPropertyChanged(nameof(TipRouter));
                _host.Settings.IsBeginnerMode = value;
                _host.SaveSettings(out _);
            }
        }
    }

    public bool IsAdvancedMode => !_isBeginnerMode;

    /// <summary>Pages that only exist in Advanced mode (tools for digging into the measurements themselves).</summary>
    public static readonly HashSet<string> AdvancedOnlyTabs = new() { "Monitoring", "RoutePath", "Adapter", "ProbeLog" };

    /// <summary>The Mark lag shortcut as set in Settings, for the Help page.</summary>
    public string MarkLagKeyText => _host.Settings.MarkLagHotkey == HotkeyGesture.Off ? "(not set)" : _host.Settings.MarkLagHotkey;

    /// <summary>Sidebar order, used by the Ctrl+1 ... Ctrl+0 shortcuts.</summary>
    private static readonly string[] PageOrder = { "Dashboard", "GameMonitor", "Activity", "Monitoring", "RoutePath", "Bufferbloat", "SpeedTest", "Adapter", "ProbeLog", "Events", "History" };

    /// <summary>The pages the sidebar currently shows, top to bottom (Beginner hides some).</summary>
    public IReadOnlyList<string> VisiblePages => PageOrder.Where(p => !IsBeginnerMode || !AdvancedOnlyTabs.Contains(p)).ToList();

    /// <summary>Ctrl+1 opens the first page of the sidebar, Ctrl+2 the second, and so on (Ctrl+0 is the tenth).</summary>
    public bool GoToPage(int index)
    {
        var pages = VisiblePages;
        if (index < 0 || index >= pages.Count) return false;
        CurrentTab = pages[index];
        return true;
    }

    public string ModeSummary => IsBeginnerMode
        ? "Beginner: plain-language verdicts and the essentials. Switch to Advanced for per-server statistics, raw connection tables, route tracing, adapter details and the live probe log."
        : "Advanced: every page and number, including per-server statistics, raw socket tables, route tracing, adapter details and the live probe log.";

    /// <summary>The one-time "how much detail do you want?" card on the Overview.</summary>
    public bool ShowModeChooser => !_host.Settings.ModeChosen;
    private RelayCommand? _chooseMode;
    public RelayCommand ChooseModeCommand => _chooseMode ??= new RelayCommand(p =>
    {
        bool advanced = string.Equals(p?.ToString(), "Advanced", StringComparison.OrdinalIgnoreCase);
        _host.Settings.ModeChosen = true;
        if (IsBeginnerMode == !advanced) { _host.SaveSettings(out _); OnPropertyChanged(nameof(ShowModeChooser)); }
        else IsBeginnerMode = !advanced;
    });

    public bool IsComparingPaths
    {
        get => _isComparingPaths;
        set => SetProperty(ref _isComparingPaths, value);
    }

    public string StatusHeadline
    {
        get => _statusHeadline;
        set => SetProperty(ref _statusHeadline, value);
    }

    public string StatusCategory
    {
        get => _statusCategory;
        set => SetProperty(ref _statusCategory, value);
    }

    public string LikelyCause
    {
        get => _likelyCause;
        set => SetProperty(ref _likelyCause, value);
    }

    public string RecommendedAction
    {
        get => _recommendedAction;
        set => SetProperty(ref _recommendedAction, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => SetProperty(ref _statusBrush, value);
    }

    private string _statusGlyph = "\uE895";
    private Brush? _statusSoftBrush, _statusEdgeBrush;
    /// <summary>Fluent icon for the overall status (check, warning, error; sync while analysing).</summary>
    public string StatusGlyph { get => _statusGlyph; private set => SetProperty(ref _statusGlyph, value); }
    /// <summary>Tinted background and edge of the status hero, matching <see cref="StatusBrush"/>.</summary>
    public Brush StatusSoftBrush { get => _statusSoftBrush ??= Res("BlueSoftBrush"); private set => SetProperty(ref _statusSoftBrush, value); }
    public Brush StatusEdgeBrush { get => _statusEdgeBrush ??= Res("BlueEdgeBrush"); private set => SetProperty(ref _statusEdgeBrush, value); }
    private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Transparent;

    public string AppVersionText => $"NetPulse {AppInfo.Version}";

    // ---- mini overlay ----

    /// <summary>Raised when the overlay should be shown or hidden (the window owns the overlay window itself).</summary>
    public event EventHandler? OverlayVisibilityChanged;
    private string _overlayDown = "-", _overlayUp = "-";
    public string OverlayDown { get => _overlayDown; private set => SetProperty(ref _overlayDown, value); }
    public string OverlayUp { get => _overlayUp; private set => SetProperty(ref _overlayUp, value); }
    public bool ShowOverlayTraffic => _host.Settings.OverlayShowActivity;

    public bool IsOverlayVisible
    {
        get => _host.Settings.OverlayVisible;
        set
        {
            if (_host.Settings.OverlayVisible == value) return;
            _host.Settings.OverlayVisible = value;
            _host.SaveSettings(out _);
            OnPropertyChanged();
            OverlayVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private RelayCommand? _toggleOverlay;
    public RelayCommand ToggleOverlayCommand => _toggleOverlay ??= new RelayCommand(() => IsOverlayVisible = !IsOverlayVisible);
    public string OverlayHint => _host.Settings.MarkLagHotkey == HotkeyGesture.Off
        ? "Reference ping, not your game server's ping."
        : $"{_host.Settings.MarkLagHotkey} marks lag while recording.";

    public string EvidenceSummary
    {
        get => _evidenceSummary;
        set => SetProperty(ref _evidenceSummary, value);
    }

    public double QuickGatewayLatency
    {
        get => _quickGatewayLatency;
        set => SetProperty(ref _quickGatewayLatency, value);
    }

    public double QuickInternetLatency
    {
        get => _quickInternetLatency;
        set => SetProperty(ref _quickInternetLatency, value);
    }

    public double QuickJitter
    {
        get => _quickJitter;
        set => SetProperty(ref _quickJitter, value);
    }

    public double QuickPacketLoss
    {
        get => _quickPacketLoss;
        set => SetProperty(ref _quickPacketLoss, value);
    }

    public double QuickDnsLatency
    {
        get => _quickDnsLatency;
        set => SetProperty(ref _quickDnsLatency, value);
    }

    public NetworkInterfaceInfo ActiveNic
    {
        get => _activeNic;
        set
        {
            if (!SetProperty(ref _activeNic, value)) return;
            OnPropertyChanged(nameof(AdapterStatusText));
            OnPropertyChanged(nameof(ShowWifiDetails));
            OnPropertyChanged(nameof(ShowEthernetNote));
            OnPropertyChanged(nameof(NonWifiNote));
        }
    }

    public string AdapterStatusText => string.IsNullOrEmpty(ActiveNic.OperationalStatus) ? "Adapter: checking..." : $"Adapter: {ActiveNic.OperationalStatus.ToLowerInvariant()}";
    public bool ShowWifiDetails => ActiveNic.IsWifi;
    public bool ShowEthernetNote => !ActiveNic.IsWifi;
    public string NonWifiNote => $"This PC is connected through {(string.IsNullOrEmpty(ActiveNic.InterfaceType) ? "a wired adapter" : ActiveNic.InterfaceType)}, so Wi-Fi signal details do not apply.";

    // Definitions come from the lexicon; in Advanced mode they also explain how the value is measured.
    public string TipLatency => Lexicon.Tip("latency", IsAdvancedMode);
    public string TipVariation => Lexicon.Tip("variation", IsAdvancedMode);
    public string TipLoss => Lexicon.Tip("loss", IsAdvancedMode);
    public string TipRouter => Lexicon.Tip("router", IsAdvancedMode);
    public System.Collections.Generic.IReadOnlyList<GlossaryEntry> Glossary => Lexicon.Glossary;
    public string HelpMeasuresText =>
        "NetPulse measures how your connection behaves: how fast and how steadily your router and several independent public servers answer, and whether answers go missing. "
        + "When a game is selected it also lists the network connections that game has open.\n\n"
        + "It does not measure the ping you see inside a game, and it does not know which server your match is on. Public reference servers are not game servers. "
        + "A result is a clue backed by measurements, never a verdict, and NetPulse says how confident it is.\n\n"
        + "Everything stays on this PC. The only outside services contacted are the public reference servers it pings, and the speed-test provider when you run Speed Test or Bufferbloat.";
    public string ProbeIntervalText => $" • Probing every {_host.Settings.MonitoringIntervalMs / 1000.0:0.##} s";
    public bool HasNoHops => MtrHops.Count == 0;

    private string _latestEventText = "No problems recorded since NetPulse started.";
    public string LatestEventText { get => _latestEventText; private set => SetProperty(ref _latestEventText, value); }
    private string _gameCardTitle = "No game selected";
    public string GameCardTitle { get => _gameCardTitle; private set => SetProperty(ref _gameCardTitle, value); }
    private string _gameCardDetail = "Choose a game to watch your connection while you play.";
    public string GameCardDetail { get => _gameCardDetail; private set => SetProperty(ref _gameCardDetail, value); }

    private void UpdateLatestEvent()
    {
        var e = Events.FirstOrDefault(x => x.Severity != "Info");
        LatestEventText = e == null
            ? "No problems recorded since NetPulse started."
            : $"{e.Timestamp.ToLocalTime():HH:mm:ss} - {Lexicon.EventLabel(e.EventType)}: {(e.Description.Length > 150 ? e.Description[..150] + "..." : e.Description)}";
    }

    private void PruneRemovedTargets()
    {
        var keep = _engine.GetCurrentMetrics().Select(m => m.TargetId).ToHashSet();
        foreach (var stale in Targets.Where(t => !keep.Contains(t.TargetId)).ToList()) Targets.Remove(stale);
    }

    private void UpdateGameCard()
    {
        var gm = GameMonitor;
        if (gm.SelectedProfile == null)
        {
            GameCardTitle = "No game selected";
            GameCardDetail = "Choose a game to watch your connection while you play.";
            return;
        }
        GameCardTitle = gm.SelectedProfile.Profile.DisplayName;
        GameCardDetail = (gm.IsMonitoring ? "Recording - " : "Not recording - ") + gm.ProcessStatus;
    }

    public string MtrDestination
    {
        get => _mtrDestination;
        set => SetProperty(ref _mtrDestination, value);
    }

    public bool IsMtrRunning
    {
        get => _isMtrRunning;
        set => SetProperty(ref _isMtrRunning, value);
    }

    public string MtrStatusText
    {
        get => _mtrStatusText;
        set => SetProperty(ref _mtrStatusText, value);
    }

    public string PathCompareDestA
    {
        get => _pathCompareDestA;
        set => SetProperty(ref _pathCompareDestA, value);
    }

    public string PathCompareDestB
    {
        get => _pathCompareDestB;
        set => SetProperty(ref _pathCompareDestB, value);
    }

    public string PathCompareSummary
    {
        get => _pathCompareSummary;
        set => SetProperty(ref _pathCompareSummary, value);
    }

    public bool IsBufferbloatRunning
    {
        get => _isBufferbloatRunning;
        set => SetProperty(ref _isBufferbloatRunning, value);
    }

    public int BufferbloatProgress
    {
        get => _bufferbloatProgress;
        set => SetProperty(ref _bufferbloatProgress, value);
    }

    public string BufferbloatStatus
    {
        get => _bufferbloatStatus;
        set => SetProperty(ref _bufferbloatStatus, value);
    }

    public BufferbloatResult? LatestBufferbloatResult
    {
        get => _latestBufferbloatResult;
        set
        {
            if (!SetProperty(ref _latestBufferbloatResult, value)) return;
            OnPropertyChanged(nameof(GradeBrush));
            OnPropertyChanged(nameof(HasBufferbloatResult));
            OnPropertyChanged(nameof(BufferbloatDownloadCaption));
            OnPropertyChanged(nameof(BufferbloatUploadCaption));
        }
    }

    /// <summary>"+3.1 ms over rest, at 412 Mbps": the rise, and the load it was measured under (unknown for results saved before 0.11).</summary>
    public string BufferbloatDownloadCaption => LoadCaption(_latestBufferbloatResult?.DownloadDeltaMs, _latestBufferbloatResult?.DownloadLoadMbps);
    public string BufferbloatUploadCaption => LoadCaption(_latestBufferbloatResult?.UploadDeltaMs, _latestBufferbloatResult?.UploadLoadMbps);
    private string LoadCaption(double? delta, double? mbps) =>
        _latestBufferbloatResult?.IsValid != true || delta == null ? " " : $"+{delta:F1} ms over rest" + (mbps > 0 ? $", at {mbps:F0} Mbps" : "");
    public bool HasBufferbloatResult => _latestBufferbloatResult?.IsValid == true;
    /// <summary>Ring colour for the grade: green for A+/A, amber for B/C, red for D/F.</summary>
    public Brush GradeBrush => _latestBufferbloatResult?.IsValid != true ? Res("BorderStrongBrush")
        : _latestBufferbloatResult.Grade switch { "A+" or "A" => HealthyBrush, "B" or "C" => DegradedBrush, _ => CriticalBrush };
    private string _bufferbloatLiveText = "";
    public string BufferbloatLiveText { get => _bufferbloatLiveText; private set => SetProperty(ref _bufferbloatLiveText, value); }

    public bool IsSpeedTestRunning
    {
        get => _isSpeedTestRunning;
        set => SetProperty(ref _isSpeedTestRunning, value);
    }

    public int SpeedTestProgress
    {
        get => _speedTestProgress;
        set => SetProperty(ref _speedTestProgress, value);
    }

    public double CurrentSpeedTestMbps
    {
        get => _currentSpeedTestMbps;
        set => SetProperty(ref _currentSpeedTestMbps, value);
    }

    public string SpeedTestStatus
    {
        get => _speedTestStatus;
        set => SetProperty(ref _speedTestStatus, value);
    }

    private double _downloadMbps, _uploadMbps;
    /// <summary>Download speed shown on the gauge: live while the test runs, then the result.</summary>
    public double DownloadMbps { get => _downloadMbps; private set { if (SetProperty(ref _downloadMbps, value)) { OnPropertyChanged(nameof(SpeedGaugeMax)); OnPropertyChanged(nameof(SpeedGaugeScale)); } } }
    public double UploadMbps { get => _uploadMbps; private set { if (SetProperty(ref _uploadMbps, value)) { OnPropertyChanged(nameof(SpeedGaugeMax)); OnPropertyChanged(nameof(SpeedGaugeScale)); } } }
    /// <summary>Full scale of both speed gauges: the next round step above the faster direction (at least 100 Mbps).</summary>
    public double SpeedGaugeMax
    {
        get
        {
            double top = Math.Max(100, Math.Max(_downloadMbps, _uploadMbps));
            foreach (var step in new[] { 100.0, 250, 500, 1000, 2500, 5000, 10000 }) if (top <= step) return step;
            return Math.Ceiling(top / 1000) * 1000;
        }
    }
    public string SpeedGaugeScale => $"Scale 0 - {SpeedGaugeMax:N0} Mbps";

    public SpeedTestResult? LatestSpeedTestResult
    {
        get => _latestSpeedTestResult;
        set => SetProperty(ref _latestSpeedTestResult, value);
    }

    #endregion

    // ---- Live graph (Overview) ----

    private readonly System.Windows.Threading.DispatcherTimer _liveTimer;
    private ChartData? _liveChart;
    private int _liveMinutes = 5;

    public ChartData? LiveChart { get => _liveChart; private set => SetProperty(ref _liveChart, value); }
    public ObservableCollection<LegendItem> LiveLegend { get; } = new();
    public IReadOnlyList<string> LiveChartOrder => LiveLegend.Select(l => l.TargetId).ToList();
    public IReadOnlyList<Choice> LiveWindows { get; } = new Choice[] { new("Last 2 minutes", 2), new("Last 5 minutes", 5), new("Last 15 minutes", 15) };
    public int LiveMinutes
    {
        get => _liveMinutes;
        set { if (SetProperty(ref _liveMinutes, value)) RefreshLiveChart(); }
    }
    public string LiveChartCaption =>
        $"Slowest reply in each time slice, so short spikes stay visible. Only plain pings are drawn, because they are directly comparable. "
        + $"NetPulse keeps the last {_host.Settings.HistoryWindowMinutes} min in memory; record a session to keep more.";

    private void RefreshLiveChart()
    {
        var targets = _host.Settings.Targets
            .Where(t => t.IsEnabled && t.Protocol == NetworkProtocol.Icmp)
            .Select(t => new SessionTarget { Id = t.Id, Name = t.IsGateway ? "Your router" : t.Name, Host = t.Host, Protocol = t.Protocol, Kind = t.IsGateway ? MeasurementKind.LocalGateway : MeasurementKind.ReferenceEndpoint })
            .ToList();

        // keep the legend (and the user's on/off choices) stable; only add or remove targets that changed
        var ids = targets.Select(t => t.Id).ToList();
        foreach (var gone in LiveLegend.Where(l => !ids.Contains(l.TargetId)).ToList()) LiveLegend.Remove(gone);
        foreach (var t in targets.Where(t => LiveLegend.All(l => l.TargetId != t.Id)))
            LiveLegend.Add(new LegendItem(t.Id, t.Name, NetPulse.App.Controls.SessionChart.ColorFor(t.Id, ids), RefreshLiveChart));

        var on = LiveLegend.Where(l => l.IsOn).Select(l => l.TargetId).ToHashSet();
        var minutes = Math.Min(_liveMinutes, _host.Settings.HistoryWindowMinutes);
        LiveChart = SessionChartBuilder.BuildLive(_engine.GetAllHistory(), targets.Where(t => on.Contains(t.Id)), DateTime.UtcNow, TimeSpan.FromMinutes(minutes), ChartMode.Latency);
        OnPropertyChanged(nameof(LiveChartCaption));
    }

    // ---- "Ready to play?" checklist (Overview) ----

    public ObservableCollection<ReadinessRow> ReadinessItems { get; } = new();
    private string _readinessHeadline = "Measuring...";
    private string _readinessGlyph = "\uE895";
    private Brush _readinessBrush = AnalyzingBrush;
    private DateTime _lastReadiness;
    public string ReadinessHeadline { get => _readinessHeadline; private set => SetProperty(ref _readinessHeadline, value); }
    public string ReadinessGlyph { get => _readinessGlyph; private set => SetProperty(ref _readinessGlyph, value); }
    public Brush ReadinessBrush { get => _readinessBrush; private set => SetProperty(ref _readinessBrush, value); }
    public string ReadinessScope => ReadinessResult.Scope;
    public RelayCommand OpenBufferbloatCommand => _openBufferbloat ??= new RelayCommand(() => CurrentTab = "Bufferbloat");
    private RelayCommand? _openBufferbloat;

    // ---- "Copy summary": a short text snapshot for a chat or a support ticket ----

    private string _copySummaryText = "Copy summary";
    public string CopySummaryText { get => _copySummaryText; private set => SetProperty(ref _copySummaryText, value); }
    public RelayCommand CopySummaryCommand => _copySummary ??= new RelayCommand(CopySummary);
    private RelayCommand? _copySummary;

    /// <summary>The text "Copy summary" puts on the clipboard (no IP addresses or network names).</summary>
    public string BuildStatusSummary()
    {
        var metrics = _engine.GetCurrentMetrics();
        var r = ReadinessCheck.Evaluate(metrics, ActiveNic, _latestBufferbloatResult ?? _host.LastBufferbloat, DateTime.UtcNow);
        return StatusSummary.Build(r, metrics, ActiveNic, DateTime.Now, NetPulse.Core.AppInfo.Version);
    }

    private async void CopySummary()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildStatusSummary());
            CopySummaryText = "Copied";
        }
        catch (Exception)
        {
            // another program can hold the clipboard open for a moment
            CopySummaryText = "Clipboard busy, try again";
        }
        await Task.Delay(2500);
        CopySummaryText = "Copy summary";
    }

    private void RefreshReadiness(bool force = false)
    {
        // a checklist that changes every second is unreadable; every few seconds is plenty
        if (!force && DateTime.UtcNow - _lastReadiness < TimeSpan.FromSeconds(4)) return;
        _lastReadiness = DateTime.UtcNow;

        var r = ReadinessCheck.Evaluate(_engine.GetCurrentMetrics(), ActiveNic, _latestBufferbloatResult ?? _host.LastBufferbloat, DateTime.UtcNow);
        ReadinessHeadline = r.Headline;
        UpdateBadges(r);
        (ReadinessGlyph, ReadinessBrush) = StatusVisual(r.Overall);
        for (int i = 0; i < r.Items.Count; i++)
        {
            var item = r.Items[i];
            if (i < ReadinessItems.Count && ReadinessItems[i].Id == item.Id) ReadinessItems[i].Update(item);
            else if (i < ReadinessItems.Count) ReadinessItems[i] = new ReadinessRow(item);
            else ReadinessItems.Add(new ReadinessRow(item));
        }
        while (ReadinessItems.Count > r.Items.Count) ReadinessItems.RemoveAt(ReadinessItems.Count - 1);
    }

    internal static (string Glyph, Brush Brush) StatusVisual(CheckStatus status) => status switch
    {
        CheckStatus.Good => ("\uE73E", HealthyBrush),
        CheckStatus.Fair => ("\uE7BA", DegradedBrush),
        CheckStatus.Poor => ("\uEA39", CriticalBrush),
        _ => ("\uE9CE", UnknownBrush)
    };
    private static readonly Brush UnknownBrush = Frozen(Color.FromRgb(102, 112, 133));

    private void UpdateMetrics(IReadOnlyList<TargetMetrics> metrics)
    {
        if (IsRecording) RefreshSessionStatus();
        UpdateFooterRecording();
        foreach (var m in metrics.Where(x => !x.IsGameEndpoint))
        {
            var existing = Targets.FirstOrDefault(t => t.TargetId == m.TargetId);
            if (existing != null)
            {
                existing.UpdateFromModel(m);
            }
            else
            {
                Targets.Add(new TargetMetricsViewModel(m));
            }
        }

        var gw = metrics.FirstOrDefault(m => m.IsGateway);
        if (gw != null) QuickGatewayLatency = gw.CurrentLatency;

        var dns = metrics.FirstOrDefault(m => m.IsDnsResolver);
        if (dns != null) QuickDnsLatency = dns.CurrentLatency;

        // Reference targets are different kinds of probe (a plain ping vs. an HTTPS handshake), so their latencies are not averaged
        // together. The headline numbers use the typical (median) of the plain pings; loss is the mean over all reference targets.
        var internet = metrics.Where(m => !m.IsGateway && !m.IsDnsResolver && !m.IsGameEndpoint && m.TotalProbes > 0).ToList();
        if (internet.Count > 0)
        {
            var pings = internet.Where(i => i.Protocol == NetworkProtocol.Icmp && i.ReplyCount > 0).ToList();
            var basis = pings.Count > 0 ? pings : internet;
            QuickInternetLatency = NetPulse.Core.Statistics.StatisticalCalculator.CalculateMedian(basis.Select(i => i.CurrentLatency).ToList());
            QuickJitter = NetPulse.Core.Statistics.StatisticalCalculator.CalculateMedian(basis.Select(i => i.Jitter).ToList());
            QuickPacketLoss = internet.Average(i => i.PacketLossPercent);
        }
        RefreshReadiness();   // after the headline numbers, which the tile ratings use
    }

    private DiagnosticResult? _lastDiagnosis;

    // ---- plain-word ratings on the headline tiles (Beginner) ----
    private string _pingBadge = "", _jitterBadge = "", _lossBadge = "", _routerBadge = "";
    private Brush _pingBadgeBrush = UnknownBrush, _jitterBadgeBrush = UnknownBrush, _lossBadgeBrush = UnknownBrush, _routerBadgeBrush = UnknownBrush;
    public string PingBadge { get => _pingBadge; private set => SetProperty(ref _pingBadge, value); }
    public string JitterBadge { get => _jitterBadge; private set => SetProperty(ref _jitterBadge, value); }
    public string LossBadge { get => _lossBadge; private set => SetProperty(ref _lossBadge, value); }
    public string RouterBadge { get => _routerBadge; private set => SetProperty(ref _routerBadge, value); }
    public Brush PingBadgeBrush { get => _pingBadgeBrush; private set => SetProperty(ref _pingBadgeBrush, value); }
    public Brush JitterBadgeBrush { get => _jitterBadgeBrush; private set => SetProperty(ref _jitterBadgeBrush, value); }
    public Brush LossBadgeBrush { get => _lossBadgeBrush; private set => SetProperty(ref _lossBadgeBrush, value); }
    public Brush RouterBadgeBrush { get => _routerBadgeBrush; private set => SetProperty(ref _routerBadgeBrush, value); }

    /// <summary>
    /// One or two words per headline number. Ping is judged against the servers' own normal (never a fixed number, since a
    /// distant server is not "bad"); stability and loss use the same rules of thumb as the "Ready to play?" checklist.
    /// </summary>
    /// <summary>
    /// The tile ratings are read from the same checklist evaluation as "Ready to play?", so a tile and the checklist
    /// can never disagree (they used to be judged a few seconds apart, and values near a limit flipped between them).
    /// </summary>
    private void UpdateBadges(ReadinessResult r)
    {
        CheckStatus Of(string id) => r.Items.FirstOrDefault(i => i.Id == id)?.Status ?? CheckStatus.Unknown;

        (PingBadge, PingBadgeBrush) = Of("normal") switch
        {
            CheckStatus.Unknown => (Of("loss") == CheckStatus.Unknown ? "Measuring" : "Learning your normal", UnknownBrush),
            CheckStatus.Good => ("Usual for you", HealthyBrush),
            _ => ("Higher than usual", DegradedBrush)
        };
        (JitterBadge, JitterBadgeBrush) = Of("stability") switch
        {
            CheckStatus.Unknown => ("Measuring", UnknownBrush),
            CheckStatus.Good => ("Steady", HealthyBrush),
            CheckStatus.Fair => ("Some wobble", DegradedBrush),
            _ => ("Unsteady", CriticalBrush)
        };
        (LossBadge, LossBadgeBrush) = Of("loss") switch
        {
            CheckStatus.Unknown => ("Measuring", UnknownBrush),
            CheckStatus.Poor => ("Losing packets", CriticalBrush),
            CheckStatus.Fair => ("A little", DegradedBrush),
            _ => QuickPacketLoss < 0.5 ? ("None", HealthyBrush) : ("A little", HealthyBrush)   // one lossy server alone is judged harmless
        };
        (RouterBadge, RouterBadgeBrush) = Of("router") switch
        {
            CheckStatus.Unknown => ("Measuring", UnknownBrush),
            CheckStatus.Good => ("Quick", HealthyBrush),
            CheckStatus.Fair => ("Slower than ideal", DegradedBrush),
            _ => ("Losing packets", CriticalBrush)
        };
    }

    // ---- live probe log (Advanced) ----

    private const int ProbeLogCapacity = 500;
    private readonly LinkedList<ProbeLogRow> _probeRing = new();
    private readonly List<ProbeLogRow> _probePending = new();
    private readonly object _probeLock = new();
    private System.Windows.Threading.DispatcherTimer? _probeTimer;
    private bool _probePaused, _probeFailuresOnly;
    private string _probeTarget = AllTargets;
    private const string AllTargets = "All targets";

    public ObservableCollection<ProbeLogRow> ProbeLog { get; } = new();
    public ObservableCollection<string> ProbeLogTargets { get; } = new() { AllTargets };
    public bool ProbeLogPaused { get => _probePaused; set { if (SetProperty(ref _probePaused, value)) OnPropertyChanged(nameof(ProbeLogPauseText)); } }
    public string ProbeLogPauseText => _probePaused ? "Resume" : "Pause";
    public RelayCommand ToggleProbeLogPauseCommand => _toggleProbePause ??= new RelayCommand(() => ProbeLogPaused = !ProbeLogPaused);
    private RelayCommand? _toggleProbePause;
    public bool ProbeLogFailuresOnly { get => _probeFailuresOnly; set { if (SetProperty(ref _probeFailuresOnly, value)) RebuildProbeLog(); } }
    public string ProbeLogTarget { get => _probeTarget; set { if (SetProperty(ref _probeTarget, value ?? AllTargets)) RebuildProbeLog(); } }
    public string ProbeLogSummary { get => _probeSummary; private set => SetProperty(ref _probeSummary, value); }
    private string _probeSummary = "";

    private void StartProbeLog()
    {
        _engine.MeasurementProcessed += (_, m) =>
        {
            var row = new ProbeLogRow(m);
            lock (_probeLock)
            {
                _probeRing.AddFirst(row);
                if (_probeRing.Count > ProbeLogCapacity) _probeRing.RemoveLast();
                _probePending.Add(row);
                if (_probePending.Count > ProbeLogCapacity) _probePending.RemoveRange(0, _probePending.Count - ProbeLogCapacity);
            }
        };
        // flush to the screen twice a second, and only while the page is open
        _probeTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => FlushProbeLog(), Application.Current.Dispatcher);
    }

    private bool Matches(ProbeLogRow r) => (!_probeFailuresOnly || !r.Ok) && (_probeTarget == AllTargets || r.Target == _probeTarget);

    private void FlushProbeLog()
    {
        if (CurrentTab != "ProbeLog" || _probePaused) return;
        List<ProbeLogRow> fresh;
        lock (_probeLock) { fresh = _probePending.ToList(); _probePending.Clear(); }
        foreach (var r in fresh)
        {
            if (!ProbeLogTargets.Contains(r.Target)) ProbeLogTargets.Add(r.Target);
            if (Matches(r)) ProbeLog.Insert(0, r);
        }
        while (ProbeLog.Count > ProbeLogCapacity) ProbeLog.RemoveAt(ProbeLog.Count - 1);
        UpdateProbeSummary();
    }

    private void RebuildProbeLog()
    {
        List<ProbeLogRow> all;
        lock (_probeLock) { all = _probeRing.ToList(); _probePending.Clear(); }
        ProbeLog.Clear();
        foreach (var r in all)
        {
            if (!ProbeLogTargets.Contains(r.Target)) ProbeLogTargets.Add(r.Target);
            if (Matches(r)) ProbeLog.Add(r);
        }
        UpdateProbeSummary();
    }

    private void UpdateProbeSummary()
    {
        int failed = ProbeLog.Count(r => !r.Ok);
        ProbeLogSummary = ProbeLog.Count == 0 ? "" : $"Showing the latest {ProbeLog.Count} probes ({failed} without a reply). Newest first; NetPulse keeps the last {ProbeLogCapacity} in memory.";
    }

    private void UpdateDiagnosis(DiagnosticResult diag)
    {
        _lastDiagnosis = diag;
        StatusHeadline = diag.Headline;
        StatusCategory = Lexicon.CategoryLabel(diag.Category);
        LikelyCause = diag.LikelyCause;
        RecommendedAction = diag.RecommendedAction;
        EvidenceSummary = diag.DetailSummary;

        StatusBrush = diag.Status switch
        {
            DiagnosticStatus.Critical => CriticalBrush,
            DiagnosticStatus.Degraded => DegradedBrush,
            _ => HealthyBrush
        };
        (StatusGlyph, StatusSoftBrush, StatusEdgeBrush) = diag.Status switch
        {
            DiagnosticStatus.Critical => ("\uEA39", Res("RedSoftBrush"), Res("RedEdgeBrush")),
            DiagnosticStatus.Degraded => ("\uE7BA", Res("AmberSoftBrush"), Res("AmberEdgeBrush")),
            _ => ("\uE73E", Res("GreenSoftBrush"), Res("GreenEdgeBrush"))
        };
    }

    private void UpdateMtrHops(List<RouteHop> hops)
    {
        MtrHops.Clear();
        foreach (var h in hops) MtrHops.Add(h);

        var anomalies = _routeAnalysisService.DetectAnomalies(new RoutePath
        {
            Destination = MtrDestination,
            Hops = hops,
            DestinationReached = hops.Any(h => h.IsResponding && h.HopNumber == hops.Count)
        });

        RouteAnomalies.Clear();
        foreach (var a in anomalies) RouteAnomalies.Add(a);
    }

    private async Task StartMtrAsync()
    {
        if (string.IsNullOrWhiteSpace(MtrDestination)) return;
        IsMtrRunning = true;
        CommandManager.InvalidateRequerySuggested();
        MtrStatusText = $"Tracing route path to {MtrDestination}...";
        MtrHops.Clear();
        RouteAnomalies.Clear();

        await _mtrService.StartAsync(MtrDestination.Trim(), 25, 1000);
        MtrStatusText = $"Continuous MTR active for {MtrDestination}.";
    }

    private void StopMtr()
    {
        _mtrService.Stop();
        IsMtrRunning = false;
        MtrStatusText = "MTR stopped.";
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ComparePathsAsync()
    {
        if (string.IsNullOrWhiteSpace(PathCompareDestA) || string.IsNullOrWhiteSpace(PathCompareDestB))
        {
            PathCompareSummary = "Enter two destinations to compare.";
            return;
        }

        PathCompareSummary = "Tracing both paths for comparison...";
        IsComparingPaths = true;
        CommandManager.InvalidateRequerySuggested();
        _compareCts = new CancellationTokenSource();
        try
        {
            var ct = _compareCts.Token;
            var traceA = await _tracerouteService.TraceRouteAsync(PathCompareDestA.Trim(), 20, 1500, null, ct);
            var traceB = await _tracerouteService.TraceRouteAsync(PathCompareDestB.Trim(), 20, 1500, null, ct);

            var comparison = _routeAnalysisService.ComparePaths(traceA, traceB);
            PathCompareSummary = comparison.Summary;
        }
        catch (OperationCanceledException)
        {
            PathCompareSummary = "Path comparison cancelled.";
        }
        catch (Exception ex)
        {
            PathCompareSummary = $"Path comparison error: {ex.Message}";
        }
        finally
        {
            IsComparingPaths = false;
            _compareCts?.Dispose();
            _compareCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RunBufferbloatAsync()
    {
        if (IsBufferbloatRunning) return;
        IsBufferbloatRunning = true;
        using var loadScope = _host.BeginLoadTest();   // this traffic is NetPulse's own, not "heavy traffic on this PC"
        _bufferbloatCts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        BufferbloatProgress = 5;
        BufferbloatStatus = "Initiating bufferbloat test...";

        var progress = new Progress<BufferbloatProgress>(p =>
        {
            BufferbloatProgress = p.Percent;
            BufferbloatStatus = p.StatusText;
            if (p.CurrentLatencyMs > 0) BufferbloatLiveText = $"{p.Phase}: {p.CurrentLatencyMs:F0} ms right now";
        });

        try
        {
            var res = await _bufferbloatService.RunTestAsync("1.1.1.1", progress, _bufferbloatCts.Token);
            LatestBufferbloatResult = res;
            _host.RememberBufferbloat(res);
            RefreshReadiness(force: true);
            if (!res.IsValid)
            {
                BufferbloatProgress = 0;
                BufferbloatStatus = $"Test could not complete: {res.ErrorMessage}";
                return;
            }
            BufferbloatStatus = $"Completed with Grade {res.Grade}!";
            _eventLogService.LogEvent(
                "BufferbloatTest",
                res.Grade == "D" || res.Grade == "F" ? "Warning" : "Info",
                $"Bufferbloat test finished: Grade {res.Grade} (+{Math.Max(res.DownloadDeltaMs, res.UploadDeltaMs):F0} ms delta)",
                "Bufferbloat");
        }
        catch (OperationCanceledException)
        {
            BufferbloatProgress = 0;
            BufferbloatStatus = "Bufferbloat test cancelled.";
        }
        catch (Exception ex)
        {
            BufferbloatProgress = 0;
            BufferbloatStatus = $"Test error: {ex.Message}";
        }
        finally
        {
            IsBufferbloatRunning = false;
            _bufferbloatCts?.Dispose();
            _bufferbloatCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RunSpeedTestAsync()
    {
        if (IsSpeedTestRunning) return;

        var confirm = MessageBox.Show(
            "The speed test generates substantial network download and upload traffic to measure maximum connection throughput. Proceed?",
            "Start Bandwidth Throughput Test",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsSpeedTestRunning = true;
        using var loadScope = _host.BeginLoadTest();   // this traffic is NetPulse's own, not "heavy traffic on this PC"
        _speedTestCts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        SpeedTestProgress = 5;
        SpeedTestStatus = "Starting multi-stream bandwidth test...";

        DownloadMbps = UploadMbps = 0;
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            SpeedTestProgress = p.Percent;
            CurrentSpeedTestMbps = p.CurrentSpeedMbps;
            SpeedTestStatus = p.StatusText;
            if (p.Phase == "Download") DownloadMbps = p.CurrentSpeedMbps;
            else if (p.Phase == "Upload") UploadMbps = p.CurrentSpeedMbps;
        });

        try
        {
            var res = await _speedTestService.RunSpeedTestAsync(progress, _speedTestCts.Token);
            LatestSpeedTestResult = res;
            if (res.CompletedSuccessfully) { DownloadMbps = res.DownloadSpeedMbps; UploadMbps = res.UploadSpeedMbps; }
            if (res.CompletedSuccessfully)
            {
                SpeedTestStatus = $"Finished: {res.DownloadSpeedMbps:F1} Mbps Download | {res.UploadSpeedMbps:F1} Mbps Upload";
                _eventLogService.LogEvent(
                    "SpeedTest",
                    "Info",
                    $"Throughput test: {res.DownloadSpeedMbps:F1} Mbps Down, {res.UploadSpeedMbps:F1} Mbps Up",
                    "SpeedTest");
            }
            else
            {
                SpeedTestProgress = 0;
                SpeedTestStatus = $"Speed test failed: {res.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            SpeedTestProgress = 0;
            SpeedTestStatus = "Speed test cancelled.";
        }
        catch (Exception ex)
        {
            SpeedTestProgress = 0;
            SpeedTestStatus = $"Speed test error: {ex.Message}";
        }
        finally
        {
            IsSpeedTestRunning = false;
            _speedTestCts?.Dispose();
            _speedTestCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ExportCsvAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "CSV File (*.csv)|*.csv",
            FileName = $"NetPulse_Measurements_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv"
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            var history = _engine.GetAllHistory();
            await _storageService.ExportToCsvAsync(sfd.FileName, history);
            Toast.Saved($"{history.Count:N0} measurements", sfd.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast.Error($"The measurements could not be saved. {ToastViewModel.Reason(ex)}");
        }
    }

    private async Task ExportJsonAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "JSON File (*.json)|*.json",
            FileName = $"NetPulse_Snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"
        };
        if (sfd.ShowDialog() == true)
        {
            var snapshot = new
            {
                Timestamp = DateTime.UtcNow,
                ActiveInterface = ActiveNic,
                Diagnosis = _engine.CurrentDiagnosis,
                Metrics = _engine.GetCurrentMetrics(),
                Events = Events.Take(100).ToList(),
                LatestBufferbloat = LatestBufferbloatResult,
                LatestSpeedTest = LatestSpeedTestResult
            };
            try
            {
                await _storageService.ExportToJsonAsync(sfd.FileName, snapshot);
                Toast.Saved("Snapshot", sfd.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Toast.Error($"The snapshot could not be saved. {ToastViewModel.Reason(ex)}");
            }
        }
    }

    private async Task ExportReportAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Markdown Report (*.md)|*.md|Text Report (*.txt)|*.txt",
            FileName = $"NetPulse_DiagnosticReport_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md"
        };
        if (sfd.ShowDialog() == true)
        {
            var routePath = new RoutePath
            {
                Destination = MtrDestination,
                Hops = MtrHops.ToList(),
                TotalLatencyMs = MtrHops.LastOrDefault(h => h.IsResponding)?.CurrentRttMs ?? 0
            };

            string report = await _storageService.GenerateDiagnosticReportAsync(
                ActiveNic,
                _engine.CurrentDiagnosis,
                _engine.GetCurrentMetrics(),
                Events.ToList(),
                routePath,
                LatestBufferbloatResult,
                LatestSpeedTestResult);

            try
            {
                await File.WriteAllTextAsync(sfd.FileName, report);
                Toast.Saved("Report", sfd.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Toast.Error($"The report could not be saved. {ToastViewModel.Reason(ex)}");
            }
        }
    }

    // ---- Session recording ----

    private bool _isRecording;
    private string _sessionStatus = "Not recording. Recording stores latency samples, events, gaps and disconnects locally on this PC.";

    public RelayCommand StartSessionCommand { get; private set; } = null!;
    public RelayCommand StopSessionCommand { get; private set; } = null!;
    public RelayCommand MarkLagCommand { get; private set; } = null!;
    public RelayCommand OpenSessionsFolderCommand { get; private set; } = null!;

    public bool IsRecording
    {
        get => _isRecording;
        private set => SetProperty(ref _isRecording, value);
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        private set => SetProperty(ref _sessionStatus, value);
    }

    /// <summary>Plain-language disclosure of the external service used by Speed Test and Bufferbloat.</summary>
    public string TransferDisclosure => _host.TransferProvider.Disclosure;
    public string TransferProviderTitle => $"This test uses {_host.TransferProvider.DisplayName}";

    public string FooterPrivacyText => $"Local-first \u2022 No telemetry \u2022 {_host.TransferProvider.DisplayName} only for speed tests";

    private bool _isAnyRecording;
    private string _footerRecordingText = "";
    /// <summary>True while the Game Monitor or a plain recording is running (shown in the footer on every page).</summary>
    public bool IsAnyRecording { get => _isAnyRecording; private set => SetProperty(ref _isAnyRecording, value); }
    public string FooterRecordingText { get => _footerRecordingText; private set => SetProperty(ref _footerRecordingText, value); }

    private void UpdateFooterRecording()
    {
        IsAnyRecording = _host.IsRecording;
        if (!IsAnyRecording) { FooterRecordingText = ""; return; }
        var elapsed = DateTime.UtcNow - (_host.Recorder.StartedUtc ?? DateTime.UtcNow);
        string what = GameMonitor.IsMonitoring ? $"Recording {GameMonitor.SelectedProfile?.Profile.DisplayName}" : "Recording network session";
        string key = _host.Settings.MarkLagHotkey;
        FooterRecordingText = $"{what}  {elapsed:hh\\:mm\\:ss}" + (key == HotkeyGesture.Off ? "" : $"  \u2022  {key} marks lag");
    }

    public string PrivacyFooter =>
        "Local-first: no analytics or telemetry. Continuous probes go to your router and public DNS/CDN endpoints; Speed Test and Bufferbloat contact "
        + _host.TransferProvider.DisplayName + " only when you run them.";

    private void InitializeSessionCommands()
    {
        // The Game Monitor and this plain recording share one recorder: only one can run at a time.
        StartSessionCommand = new RelayCommand(() =>
        {
            if (!_host.TryStartSession(out var error))
            {
                SessionStatus = error ?? "Could not start recording.";
                return;
            }
            IsRecording = true;
            RefreshSessionStatus();
            CommandManager.InvalidateRequerySuggested();
        }, () => !_host.IsRecording);

        StopSessionCommand = new RelayCommand(() =>
        {
            // the outcome is reported through NetPulseHost.SessionSaved (OnSessionSaved)
            if (_host.StopSession(out _) == null) SessionStatus = "No session was being recorded.";
            IsRecording = false;
            CommandManager.InvalidateRequerySuggested();
        }, () => IsRecording);

        MarkLagCommand = new RelayCommand(() =>
        {
            if (_host.MarkLag()) RefreshSessionStatus("Lag marker added.");
        }, () => IsRecording);

        OpenSessionsFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(_host.Sessions.Directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_host.Sessions.Directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SessionStatus = $"Could not open the sessions folder: {ex.Message}";
            }
        });
    }

    /// <summary>Called by the system-wide shortcut. Marks lag in whichever recording is running; false when none is.</summary>
    public bool MarkLagFromShortcut()
    {
        bool ok = _host.MarkLag("Marked with the keyboard shortcut");
        if (ok && IsRecording) RefreshSessionStatus("Lag marker added.");
        return ok;
    }

    public void OpenRecording(string id)
    {
        CurrentTab = "History";
        _ = History.OpenAsync(id);
    }

    private string? _lastSavedId;
    private RelayCommand? _openLast;
    public RelayCommand OpenLastRecordingCommand => _openLast ??= new RelayCommand(() => { if (_lastSavedId != null) OpenRecording(_lastSavedId); }, () => _lastSavedId != null && !IsRecording);

    private void OnSessionSaved(SessionSavedEventArgs e)
    {
        var s = e.Session;
        if (s.GameId == null)
        {
            IsRecording = false;
            _lastSavedId = e.Error == null ? s.Id : null;
            SessionStatus = e.Error == null
                ? $"Saved \"{s.Name}\": {s.Samples.Count:N0} probes, {s.Events.Count} events, {s.LagMarkers.Count} lag markers, {s.Gaps.Count} monitoring gaps, {s.Disconnects.Count} connection losses."
                : $"The recording was made but could not be saved: {e.Error}";
        }
        if (CurrentTab == "History") _ = History.RefreshAsync();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshSessionStatus(string? prefix = null)
    {
        if (!_host.Recorder.IsRecording) return;
        var elapsed = DateTime.UtcNow - (_host.Recorder.StartedUtc ?? DateTime.UtcNow);
        SessionStatus = $"{(prefix == null ? "" : prefix + " ")}Recording for {elapsed:hh\\:mm\\:ss} - {_host.Recorder.SampleCount:N0} samples. Press 'Mark lag' whenever you notice lag in-game.";
    }

    public void OnAppExiting()
    {
        _bufferbloatCts?.Cancel();
        _speedTestCts?.Cancel();
        _compareCts?.Cancel();
        _mtrService.Stop();
        _liveTimer.Stop();
        // The shared engine, settings and any recording session are stopped and saved by NetPulseHost.Dispose.
    }
}
