using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class NetPulseHost : IDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);

    private readonly Timer _checkpointTimer;
    private readonly object _sessionLock = new();
    private bool _disposed;

    public string DataDirectory { get; }
    public SettingsService SettingsService { get; }
    public AppSettings Settings => SettingsService.Current;

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

        Engine = new MeasurementEngine(Discovery, Ping, Tcp, Http, Dns, Diagnostics, Events) { Settings = Settings };

        Recorder = new SessionRecorder();
        Sessions = new SessionStore(Path.Combine(DataDirectory, "sessions"));

        Engine.MeasurementProcessed += (_, m) => Recorder.RecordMeasurement(m);
        Engine.CycleCompleted += (_, t) => Recorder.RecordCycle(t);
        Events.EventLogged += (_, e) => Recorder.RecordEvent(e);
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
    }

    public void Start() => Engine.Start();

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
            return session;
        }
    }

    private void Checkpoint()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            TrySave(Recorder.Snapshot());
        }
    }

    private void TrySave(MonitoringSession? snapshot)
    {
        if (snapshot == null) return;
        try
        {
            Sessions.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // checkpointing is best effort; the final save on Stop reports failures
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
        GameMonitor.Dispose(); // stops and saves a game session first, so nothing recorded is lost
        Mtr.Stop();
        Engine.Stop();
        StopSession(out _);
        SettingsService.TrySave(out _);
    }
}
