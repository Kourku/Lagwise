using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NetPulse.Core.Games;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Services;

/// <summary>
/// Composition root: builds every service exactly once so the main window, the system tray and the future
/// Game Monitor all share the same measurement engine, event log, settings and session recorder.
/// Contains no UI types.
/// </summary>
public sealed class SessionSavedEventArgs : EventArgs
{
    public SessionSavedEventArgs(MonitoringSession session, string? error) { Session = session; Error = error; }
    public MonitoringSession Session { get; }
    /// <summary>Null when the file was written successfully.</summary>
    public string? Error { get; }
}

public sealed class NetPulseHost : IDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);

    private readonly Timer _checkpointTimer;
    private Timer? _activityTimer;
    private int _loadTests;
    private DateTime _loadTestEndedUtc;
    private readonly object _sessionLock = new();
    private bool _disposed;

    public string DataDirectory { get; }
    public SettingsService SettingsService { get; }
    public AppSettings Settings => SettingsService.Current;

    /// <summary>Network adapter activity (throughput, packets, errors), sampled once a second while running.</summary>
    public AdapterActivityMonitor Activity { get; }

    public NetworkDiscoveryService Discovery { get; }
    public PingService Ping { get; }
    public TcpProbeService Tcp { get; }
    public HttpProbeService Http { get; }
    public DnsProbeService Dns { get; }
    public DiagnosticEngine Diagnostics { get; }
    public EventLogService Events { get; }
    public StorageService Storage { get; }
    /// <summary>Null on non-Windows platforms.</summary>
    public WindowsIntegrationService? WindowsIntegration { get; }
    public TracerouteService Traceroute { get; }
    public MtrService Mtr { get; }
    public RouteAnalysisService RouteAnalysis { get; }
    public ITransferProvider TransferProvider { get; }
    public BufferbloatService Bufferbloat { get; }
    public SpeedTestService SpeedTest { get; }
    public MeasurementEngine Engine { get; }
    public SessionRecorder Recorder { get; }
    public SessionStore Sessions { get; }
    public GameMonitorService GameMonitor { get; }
    /// <summary>Speed and Bufferbloat results and ping seen under real load, kept across restarts (<c>test-history.json</c>).</summary>
    public TestHistory Tests { get; }
    /// <summary>How the connection did hour by hour while NetPulse runs, recording or not, and every drop (<c>diary.json</c>).</summary>
    public ConnectionDiary Diary { get; }
    /// <summary>Is a site down, or blocked between this PC and it? Contacts sites only when asked.</summary>
    public Sites.SiteCheckService SiteChecks { get; }
    /// <summary>Is this PC and its connection safe? The local part sends nothing; the network part runs when asked.</summary>
    public Security.SecurityCheckService Security { get; }

    /// <param name="processSource">Override for tests; defaults to the real Windows process list.</param>
    /// <param name="connectionSource">Override for tests; defaults to the Windows TCP/UDP socket tables.</param>
    public NetPulseHost(string? dataDirectory = null, IProcessSource? processSource = null, IConnectionSource? connectionSource = null, IReadOnlyList<GameProfile>? profiles = null)
    {
        DataDirectory = dataDirectory ?? SettingsService.DefaultDirectory();
        SettingsService = new SettingsService(Path.Combine(DataDirectory, "settings.json"));
        SettingsService.Load();

        Discovery = new NetworkDiscoveryService();
        Ping = new PingService();
        Tcp = new TcpProbeService();
        Http = new HttpProbeService();
        Dns = new DnsProbeService();
        Diagnostics = new DiagnosticEngine();
        Events = new EventLogService { DedupWindow = TimeSpan.FromSeconds(Settings.EventDedupSeconds) };
        Storage = new StorageService();
        if (OperatingSystem.IsWindows()) WindowsIntegration = new WindowsIntegrationService();
        Traceroute = new TracerouteService();
        Mtr = new MtrService(Traceroute);
        RouteAnalysis = new RouteAnalysisService();
        TransferProvider = TransferProviders.Resolve(Settings.TransferProviderId);
        Bufferbloat = new BufferbloatService(Ping, TransferProvider);
        SpeedTest = new SpeedTestService(TransferProvider);

        Engine = new MeasurementEngine(Discovery, Ping, Tcp, Http, Dns, Diagnostics, Events)
        {
            Settings = Settings,
            Baselines = new Statistics.BaselineMemory(Path.Combine(DataDirectory, "baselines.json"))
        };

        Recorder = new SessionRecorder();
        Sessions = new SessionStore(Path.Combine(DataDirectory, "sessions"));
        // versions before 0.14 only remembered the last bufferbloat result: it starts the history
        Tests = new TestHistory(Path.Combine(DataDirectory, "test-history.json"),
            () => LastBufferbloat is { } b ? new[] { TestRecord.FromBufferbloat(b, "") } : Array.Empty<TestRecord>());

        Diary = new ConnectionDiary(Path.Combine(DataDirectory, "diary.json"));
        SiteChecks = new Sites.SiteCheckService(internetDown: () => Engine.IsInternetDown);
        Security = new Security.SecurityCheckService(() =>
        {
            var nic = Engine.CurrentNic;
            return new Security.SecurityCheckService.NetworkInterfaceInfoView(nic.IsWifi, nic.WifiAuthentication, nic.WifiCipher);
        });

        Engine.MeasurementProcessed += (_, m) =>
        {
            Recorder.RecordMeasurement(m);
            if (IsReferencePing(m)) Diary.AddProbe(m.Timestamp, ConnectionDiary.KeyOf(m.Protocol, m.TargetHost), m.TargetName, m.Success, m.RttMs);
        };
        Engine.CycleCompleted += (_, t) =>
        {
            Recorder.RecordCycle(t);
            Diary.AddCycle(t, Settings.MonitoringIntervalMs);
            if (_lastBaselineSample == default) _lastBaselineSample = t;
            else if (t - _lastBaselineSample >= Statistics.BaselineMemory.SampleInterval)
            {
                _lastBaselineSample = t;
                Engine.SampleBaselines(t);
            }
        };
        Events.EventLogged += (_, e) =>
        {
            Recorder.RecordEvent(e);
            if (e.TargetName != SessionRecorder.ConnectivityScope) return;
            if (e.EventType == "Disconnected") Diary.OutageStarted(e.Timestamp, e.ContextData == MeasurementEngine.BeyondRouterContext);
            else if (e.EventType == "Restored") Diary.OutageEnded(e.Timestamp);
        };
        Recorder.GapDetected += (_, gap) => Events.LogEvent("MonitoringGap", "Info",
            $"NetPulse was not measuring for {gap.DurationSeconds:F0} s (system sleep or stalled monitoring). This is not a network outage.",
            timestamp: gap.EndUtc);

        GameMonitor = new GameMonitorService(
            Engine, Recorder, Events, () => Settings,
            processSource ?? new SystemProcessSource(GameProfiles.AllProcessNames),
            connectionSource ?? new WindowsConnectionSource(),
            (gameId, name) => StartSession(gameId, name),
            (out string? error) => StopSession(out error),
            profiles: profiles);

        _checkpointTimer = new Timer(_ => Checkpoint(), null, CheckpointInterval, CheckpointInterval);

        Activity = new AdapterActivityMonitor
        {
            HeavyTrafficBps = Settings.HeavyTrafficMbps > 0 ? Settings.HeavyTrafficMbps * 1_000_000.0 : double.PositiveInfinity,
            // NetPulse's own speed and bufferbloat tests are heavy traffic on purpose; so are their last few seconds
            SuppressHeavyTraffic = () => Volatile.Read(ref _loadTests) > 0 || DateTime.UtcNow - _loadTestEndedUtc < TimeSpan.FromSeconds(5)
        };
        Activity.HeavyTrafficDetected += (_, h) =>
        {
            // the minute before is remembered now: a long stretch outlasts the probe history
            if (IsInternetAdapter(h.AdapterId))
                lock (_loadLock) _pingBeforeLoad[h.AdapterId] = PingUnderLoad.Before(Engine.GetAllHistory(), ReferencePingIds(), h.StartUtc, _lastLoadEndUtc);

            // name the programs that could be behind it; Windows does not say which program moved the bytes
            string who = OnlinePrograms.HeavyCandidatesText(ProgramsOnline());
            Events.LogEvent("HeavyTraffic", GameMonitor.IsMonitoring ? "Warning" : "Info",
                $"Heavy traffic on this PC ({h.Adapter}): up to {AdapterActivityMonitor.FormatBps(h.RxBps)} down and {AdapterActivityMonitor.FormatBps(h.TxBps)} up for {h.Duration.TotalSeconds:F0} s. " +
                (who.Length > 0 ? $"Programs online at the time that often download or upload a lot: {who}. " : "") +
                "Downloads, updates and uploads compete with games for your connection. Other devices at home are not included.",
                h.Adapter, h.RxBps / 1e6);
        };
        Activity.HeavyTrafficEnded += (_, h) => OnHeavyTrafficEnded(h);
        // while recording, keep each second of traffic on the internet adapter with the recording
        Activity.Sampled += (_, _) =>
        {
            if (!Recorder.IsRecording) return;
            var a = InternetAdapterActivity();
            if (a?.Latest is { } p) Recorder.RecordThroughput(p.T, p.RxBps, p.TxBps);
        };
    }

    private DateTime _lastBaselineSample;

    private readonly object _loadLock = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, double>> _pingBeforeLoad = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastLoadEndUtc;

    /// <summary>
    /// A stretch of heavy traffic is over: notes in the timeline what it did to ping (passive bufferbloat) and keeps the
    /// observation with the test results. Only the adapter Windows uses for the internet counts, so a VPN adapter
    /// carrying the same traffic is not counted twice.
    /// </summary>
    private void OnHeavyTrafficEnded(HeavyTrafficStretch h)
    {
        IReadOnlyDictionary<string, double>? before;
        lock (_loadLock)
        {
            if (!_pingBeforeLoad.Remove(h.AdapterId, out before)) return;
            _lastLoadEndUtc = h.EndUtc;
        }
        var ping = PingUnderLoad.During(Engine.GetAllHistory(), ReferencePingIds(), before, h.StartUtc, h.EndUtc);
        Events.LogEvent("HeavyTrafficEnded", ping?.RiseMs > PingUnderLoad.ClearRiseMs ? "Warning" : "Info",
            PingUnderLoad.Describe(h, ping), h.Adapter, ping?.RiseMs, timestamp: h.EndUtc);
        if (ping == null) return;
        Tests.Add(new TestRecord
        {
            T = h.EndUtc, Kind = TestKind.RealLoad, Connection = TestRecord.ConnectionOf(Engine.CurrentNic),
            RestMs = ping.RestMs, LoadedMs = ping.LoadedMs, RiseMs = ping.RiseMs, LossPercent = ping.LossPercent,
            DownMbps = Math.Round(h.AverageRxBps / 1e6, 1), UpMbps = Math.Round(h.AverageTxBps / 1e6, 1),
            PeakDownMbps = Math.Round(h.PeakRxBps / 1e6, 1), PeakUpMbps = Math.Round(h.PeakTxBps / 1e6, 1),
            Seconds = (int)Math.Round(h.Duration.TotalSeconds)
        });
    }

    /// <summary>A plain ping to a reference server from Settings (not the router, not the DNS lookup, not a game endpoint).</summary>
    private bool IsReferencePing(Measurement m) =>
        m.Protocol == NetworkProtocol.Icmp
        && Settings.Targets.FirstOrDefault(t => t.Id == m.TargetId) is { IsGateway: false, IsDnsResolver: false };

    private bool IsInternetAdapter(string adapterId) =>
        string.IsNullOrEmpty(adapterId) || string.Equals(InternetAdapterActivity()?.Id, adapterId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain-ping reference servers: what "your ping" means on the traffic graph and under load.</summary>
    public IReadOnlySet<string> ReferencePingIds() =>
        Engine.GetCurrentMetrics()
            .Where(m => !m.IsGateway && !m.IsDnsResolver && !m.IsGameEndpoint && m.Protocol == NetworkProtocol.Icmp)
            .Select(m => m.TargetId).ToHashSet();

    /// <summary>Raised after a recording has been stopped and saved (or failed to save), from any source: the History tab, the Game Monitor or automatic recording.</summary>
    public event EventHandler<SessionSavedEventArgs>? SessionSaved;

    public void Start()
    {
        _activityTimer ??= new Timer(_ => Activity.Sample(DateTime.UtcNow), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Engine.Start();
        ApplyRetention();
    }

    /// <summary>Deletes recordings older than the retention setting, in the background. Returns immediately.</summary>
    public void ApplyRetention()
    {
        int days = Settings.RecordingRetentionDays;
        if (days <= 0) return;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                int n = Sessions.DeleteOlderThan(cutoff);
                if (n > 0) Events.LogEvent("Recordings", "Info", $"Deleted {n} recording(s) older than {days} days, as set in Settings.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // retention is best effort
            }
        });
    }

    /// <summary>Applies the heavy-traffic threshold from Settings (0 = off).</summary>
    public void ApplyActivitySettings() =>
        Activity.HeavyTrafficBps = Settings.HeavyTrafficMbps > 0 ? Settings.HeavyTrafficMbps * 1_000_000.0 : double.PositiveInfinity;

    /// <summary>The adapter Windows uses for the internet (the engine's active interface), or the busiest-capable one.</summary>
    public AdapterActivity? InternetAdapterActivity()
    {
        var all = Activity.Snapshot();
        string? id = Engine.CurrentNic?.Id;
        return all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }

    private static readonly IReadOnlyDictionary<string, (string Name, ProgramKind Kind)> GameProcessNames = OnlinePrograms.FromProfiles(GameProfiles.All);
    private readonly WindowsConnectionSource _programSockets = new();

    /// <summary>Programs on this PC with internet connections right now (connections only: Windows does not report bytes per program).</summary>
    public IReadOnlyList<OnlineProgram> ProgramsOnline()
    {
        try
        {
            var snapshot = _programSockets.Capture();
            return OnlinePrograms.Summarize(snapshot.Connections, OnlinePrograms.ProcessName, GameProcessNames, Environment.ProcessId);
        }
        catch (Exception)
        {
            return Array.Empty<OnlineProgram>();
        }
    }

    /// <summary>Marks a speed or bufferbloat test as running, so its traffic is not reported as heavy traffic. Dispose when it ends.</summary>
    public IDisposable BeginLoadTest()
    {
        Interlocked.Increment(ref _loadTests);
        return new LoadTestScope(this);
    }

    private sealed class LoadTestScope : IDisposable
    {
        private NetPulseHost? _host;
        public LoadTestScope(NetPulseHost host) => _host = host;
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _host, null);
            if (h == null) return;
            h._loadTestEndedUtc = DateTime.UtcNow;
            Interlocked.Decrement(ref h._loadTests);
        }
    }

    // ---- last bufferbloat result: kept on disk so the "ready to play" checklist remembers it across restarts

    private string BufferbloatFile => Path.Combine(DataDirectory, "last-bufferbloat.json");
    private BufferbloatResult? _lastBufferbloat;
    private bool _bufferbloatLoaded;

    /// <summary>The most recent valid bufferbloat result (this run or an earlier one), or null.</summary>
    public BufferbloatResult? LastBufferbloat
    {
        get
        {
            if (_bufferbloatLoaded) return _lastBufferbloat;
            _bufferbloatLoaded = true;
            try
            {
                if (File.Exists(BufferbloatFile))
                    _lastBufferbloat = System.Text.Json.JsonSerializer.Deserialize<BufferbloatResult>(File.ReadAllText(BufferbloatFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _lastBufferbloat = null; // unreadable: behave as never tested
            }
            return _lastBufferbloat;
        }
    }

    /// <summary>Remembers a completed bufferbloat test, also in the test history. Invalid (failed) results are ignored.</summary>
    public void RememberBufferbloat(BufferbloatResult result)
    {
        if (!result.IsValid) return;
        _ = LastBufferbloat;   // seeds the history from the old file first, so this result is not added twice
        _ = Tests.All();
        _lastBufferbloat = result;
        _bufferbloatLoaded = true;
        Tests.Add(TestRecord.FromBufferbloat(result, TestRecord.ConnectionOf(Engine.CurrentNic)));
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(BufferbloatFile + ".tmp", System.Text.Json.JsonSerializer.Serialize(result));
            File.Move(BufferbloatFile + ".tmp", BufferbloatFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still remembered for this run
        }
    }

    /// <summary>
    /// The largest ping rise seen in real use in the last two weeks, or null. The largest, not the newest: a steady ping
    /// during a lighter download does not undo a clear rise during a heavier one.
    /// </summary>
    public TestRecord? WorstRecentRealLoad(DateTime nowUtc) =>
        Tests.Of(TestKind.RealLoad).Where(r => nowUtc - r.T <= ReadinessCheck.RealLoadRelevance).MaxBy(r => r.RiseMs);

    /// <summary>Keeps a completed speed test in the test history. Failed tests are not kept.</summary>
    public void RememberSpeedTest(SpeedTestResult result)
    {
        if (!result.CompletedSuccessfully) return;
        Tests.Add(TestRecord.FromSpeed(result, TestRecord.ConnectionOf(Engine.CurrentNic)));
    }

    /// <summary>True while any recording (plain or Game Monitor) is running.</summary>
    public bool IsRecording => Recorder.IsRecording;

    /// <summary>
    /// Marks a moment of lag in whatever is being recorded: the Game Monitor session if one runs, otherwise a plain
    /// network recording. Returns false when nothing is being recorded.
    /// </summary>
    public bool MarkLag(string? note = null)
    {
        if (GameMonitor.IsMonitoring) return GameMonitor.MarkLag(note);
        if (!Recorder.IsRecording) return false;

        var now = DateTime.UtcNow;
        Recorder.MarkLag(note, now);
        Events.LogEvent("LagMarker", "Info", string.IsNullOrWhiteSpace(note) ? "You marked a moment of lag." : $"You marked lag: {note.Trim()}", timestamp: now);
        return true;
    }

    /// <summary>Starts a plain network recording. Fails (with a reason) when a recording is already running.</summary>
    public bool TryStartSession(out string? error)
    {
        lock (_sessionLock)
        {
            if (Recorder.IsRecording)
            {
                error = GameMonitor.IsMonitoring
                    ? "The Game Monitor is already recording. Stop it there first, or use Mark lag in the Game Monitor."
                    : "A recording is already running.";
                return false;
            }
            StartSession();
            error = null;
            return true;
        }
    }

    /// <summary>Persists current settings; returns false with the reason on failure.</summary>
    public bool SaveSettings(out string? error) => SettingsService.TrySave(out error);

    public MonitoringSession StartSession(string? gameId = null, string? name = null)
    {
        lock (_sessionLock)
        {
            var session = Recorder.Start(gameId, name, Settings.Targets, Engine.CurrentNic, Settings.MonitoringIntervalMs);
            TrySave(Recorder.Snapshot());
            return session;
        }
    }

    /// <summary>Stops the recording and saves it. Returns null if no session was active.</summary>
    public MonitoringSession? StopSession(out string? saveError)
    {
        lock (_sessionLock)
        {
            saveError = null;
            var session = Recorder.Stop();
            if (session == null) return null;

            try
            {
                Sessions.Save(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                saveError = ex.Message;
            }
            SessionSaved?.Invoke(this, new SessionSavedEventArgs(session, saveError));
            return session;
        }
    }

    private int _checkpointedSamples = -1;

    private void Checkpoint()
    {
        lock (_sessionLock)
        {
            if (_disposed || !Recorder.IsRecording) { _checkpointedSamples = -1; return; }
            // a long recording is several MB of JSON: do not rewrite it when nothing was recorded since the last checkpoint
            int samples = Recorder.SampleCount;
            if (samples == _checkpointedSamples) return;
            if (TrySave(Recorder.Snapshot())) _checkpointedSamples = samples;
        }
    }

    private bool TrySave(MonitoringSession? snapshot)
    {
        if (snapshot == null) return false;
        try
        {
            Sessions.Save(snapshot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // checkpointing is best effort; the final save on Stop reports failures
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _checkpointTimer.Dispose();
        _activityTimer?.Dispose();
        GameMonitor.Dispose(); // stops and saves a game session first, so nothing recorded is lost
        Mtr.Stop();
        Engine.Stop();
        Diary.Save();   // the hour in progress, so a restart continues it
        StopSession(out _);
        SettingsService.TrySave(out _);
    }
}
