using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPulse.Core.Models;
using NetPulse.Core.Services;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Games;

public delegate MonitoringSession? StopSessionDelegate(out string? error);

/// <param name="Occurrences">How many separate connections this row stands for (repeated reconnects to the same endpoint are grouped).</param>
public sealed record ConnectionView(
    string Id, string Protocol, string Local, string Remote, string Scope, string Role, string State, int Pid,
    DateTime FirstSeenUtc, DateTime LastSeenUtc, bool IsActive, bool HasRemote, string? MeasuredBy, string MeasureHint,
    ConnectionKind Kind = ConnectionKind.Other, string KindLabel = "", string KindExplanation = "", int Occurrences = 1);

public sealed record MeasuredEndpointView(string TargetId, string Name, string Method, string Caveat, DateTime StartedUtc, bool ConnectionStillObserved);

public sealed class GameMonitorSnapshot
{
    public GameProfile? Profile { get; init; }
    public GameProcessState? Process { get; init; }
    public bool IsMonitoring { get; init; }
    public DateTime? StartedUtc { get; init; }
    public IReadOnlyList<ConnectionView> Connections { get; init; } = Array.Empty<ConnectionView>();
    /// <summary>Distinct remote address:port pairs currently observed with a public address.</summary>
    public int DistinctRemoteEndpoints { get; init; }
    public int UdpSocketCount { get; init; }
    /// <summary>Active TCP connections to public web ports (HTTPS), i.e. probably not match traffic.</summary>
    public int WebServiceCount { get; init; }
    /// <summary>True when the game is running and owns UDP sockets: its match traffic is most likely there, out of Windows' sight.</summary>
    public bool MatchTrafficHidden { get; init; }
    public IReadOnlyList<MeasuredEndpointView> Measured { get; init; } = Array.Empty<MeasuredEndpointView>();
    public IReadOnlyList<GameFinding> Findings { get; init; } = Array.Empty<GameFinding>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Orchestrates the Game Monitor: watches for the selected game's process, discovers its sockets, records everything into
/// the shared session recorder, measures endpoints the user picks through the shared measurement engine, and runs the
/// correlating diagnostic analyzer. Process polling is a single light background loop; connection capture only happens
/// while monitoring and only when the game is running.
/// </summary>
public sealed class GameMonitorService : IDisposable
{
    private readonly object _lock = new();
    private readonly MeasurementEngine _engine;
    private readonly SessionRecorder _recorder;
    private readonly EventLogService _events;
    private readonly Func<AppSettings> _settings;
    private readonly IProcessSource _processes;
    private readonly IConnectionSource _connections;
    private readonly Func<string?, string?, MonitoringSession> _startSession;
    private readonly StopSessionDelegate _stopSession;
    private readonly Func<DateTime> _clock;
    private readonly IReadOnlyList<GameProfile> _profiles;

    private readonly ConnectionTracker _tracker = new();
    private readonly List<ConnectionChange> _recentChanges = new();
    private readonly Dictionary<string, (string ConnectionId, EndpointMethod Method, DateTime StartedUtc, string Caveat, string Name)> _measured = new();
    private readonly HashSet<string> _reportedWarnings = new();

    private GameProfile? _profile;
    private GameWatcher? _watcher;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _monitoring;
    private bool _autoStarted;
    private DateTime? _notRunningSinceUtc;
    private DateTime? _startedUtc;
    private IReadOnlyList<GameFinding> _findings = Array.Empty<GameFinding>();
    private IReadOnlyList<string> _warnings = Array.Empty<string>();
    private bool _disposed;

    /// <summary>Poll period while monitoring (process list + socket tables).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Poll period while a game is selected but not being monitored (process list only).</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// With automatic recording, how long the game must stay closed before the recording is stopped and saved.
    /// Bridges quick restarts and the gap between a game's processes (for example League's loading screen).
    /// </summary>
    public TimeSpan AutoStopGrace { get; set; } = TimeSpan.FromSeconds(20);
    public event EventHandler? Updated;
    /// <summary>
    /// The selected game was started while NetPulse watched for it (not when it was already running as watching began).
    /// Raised from the polling thread, whether or not a recording runs.
    /// </summary>
    public event EventHandler<GameProfile>? GameLaunched;

    public GameMonitorService(
        MeasurementEngine engine, SessionRecorder recorder, EventLogService events, Func<AppSettings> settings,
        IProcessSource processes, IConnectionSource connections,
        Func<string?, string?, MonitoringSession> startSession, StopSessionDelegate stopSession, Func<DateTime>? clock = null,
        IReadOnlyList<GameProfile>? profiles = null)
    {
        _profiles = profiles ?? GameProfiles.All;
        _engine = engine;
        _recorder = recorder;
        _events = events;
        _settings = settings;
        _processes = processes;
        _connections = connections;
        _startSession = startSession;
        _stopSession = stopSession;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool IsMonitoring { get { lock (_lock) return _monitoring; } }
    /// <summary>True when the current recording was started by automatic recording (and will stop when the game closes).</summary>
    public bool StartedAutomatically { get { lock (_lock) return _monitoring && _autoStarted; } }
    public GameProfile? SelectedProfile { get { lock (_lock) return _profile; } }

    // ------------------------------------------------------------------ selection / loop

    /// <summary>Chooses the game to watch and starts light-weight process detection. Not allowed while recording.</summary>
    public bool Select(string? profileId, out string message)
    {
        CancellationTokenSource? oldCts;
        Task? oldTask;
        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            if (_monitoring) { message = "Stop monitoring before changing the game."; return false; }
            (oldCts, oldTask) = (_loopCts, _loopTask);
            (_loopCts, _loopTask) = (null, null);
        }

        // wait for the previous loop outside the lock: the loop itself takes the lock
        StopLoop(oldCts, oldTask);

        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            _tracker.Clear();
            _recentChanges.Clear();
            _findings = Array.Empty<GameFinding>();
            _profile = _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
            _watcher = _profile == null ? null : new GameWatcher(_profile);
            if (_profile != null)
            {
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _loopTask = Task.Run(() => RunLoopAsync(token));
            }
            message = _profile == null ? "No game selected." : $"Watching for {_profile.DisplayName}.";
        }

        RaiseUpdated();
        return true;
    }

    private static void StopLoop(CancellationTokenSource? cts, Task? task)
    {
        if (cts == null) return;
        cts.Cancel();
        try { task?.Wait(3000); } catch (AggregateException) { }
        cts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogOnce("poll-error:" + ex.GetType().Name, "GameMonitor", "Warning", $"Game monitoring poll failed: {ex.Message}");
                }

                // detect the game cheaply and slowly while idle; poll faster only while actually monitoring
                var delay = IsMonitoring ? PollInterval : IdlePollInterval;
                await Task.Delay(delay < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : delay, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    // ------------------------------------------------------------------ one poll (public for deterministic tests)

    /// <summary>
    /// One poll at a time: the process list is read outside <c>_lock</c> and applied afterwards, so two overlapping polls
    /// could apply an older reading ("game still running") after a newer one ("game closed"), resetting the automatic
    /// stop countdown and logging a false restart. Overlap can happen when the loop is restarted while a slow poll is
    /// still running, or when a caller polls directly.
    /// </summary>
    private readonly object _pollLock = new();

    public void PollOnce()
    {
        lock (_pollLock) PollOnceCore();
    }

    private void PollOnceCore()
    {
        // Process and socket enumeration are done outside the lock: they are the slow parts.
        GameWatcher? watcher;
        bool monitoring;
        lock (_lock)
        {
            watcher = _watcher;
            monitoring = _monitoring;
        }
        if (watcher == null) return;

        var now = _clock();
        var processes = _processes.GetProcesses();

        WatchUpdate update;
        lock (_lock)
        {
            if (_watcher != watcher) return; // selection changed while enumerating
            update = watcher.Observe(processes, now);
            monitoring = _monitoring;
        }
        if (watcher.Observations > 1 && update.Changes.Any(c => c.Kind == GameProcessChangeKind.GameStarted))
            GameLaunched?.Invoke(this, watcher.Profile);

        if (!monitoring && update.State.State == GameRunState.Running && _settings().AutoRecordGame && !_recorder.IsRecording)
        {
            monitoring = StartCore(automatic: true, out _);
        }
        else if (monitoring && AutoStopDue(update.State.State, now))
        {
            Stop(out _);
            return;
        }

        if (!monitoring)
        {
            RaiseUpdated();
            return;
        }

        bool running = update.State.State == GameRunState.Running;
        var snapshot = running ? _connections.Capture() : new ConnectionSnapshot(Array.Empty<RawConnection>(), Array.Empty<string>(), now);
        var pids = update.State.GamePids;

        lock (_lock)
        {
            if (!_monitoring) return;

            foreach (var change in update.Changes)
            {
                _events.LogEvent("GameProcess", change.Kind == GameProcessChangeKind.GameExited ? "Warning" : "Info", change.Description, _profile!.DisplayName, timestamp: change.AtUtc);
            }

            foreach (var p in update.State.GameProcesses) _recorder.UpsertGameProcess(p, now);
            _recorder.MarkGameProcessesExited(update.State.GameProcesses.Select(p => p.Identity).ToHashSet(), now);

            var cu = _tracker.Update(snapshot with { CapturedUtc = now }, pids);
            foreach (var c in _tracker.All.Where(c => c.IsActive).Concat(cu.Changes.Select(ch => ch.Connection)).DistinctBy(c => c.Id))
                _recorder.UpsertConnection(c);

            LogConnectionChanges(cu.Changes);
            _recentChanges.AddRange(cu.Changes);
            int window = _settings().RecentWindowSeconds;
            _recentChanges.RemoveAll(c => now - c.AtUtc > TimeSpan.FromSeconds(window));

            foreach (var w in cu.Warnings)
                LogOnce("conn-warning:" + w, "GameMonitor", "Warning", "Connection discovery limited: " + w);
            _warnings = cu.Warnings;

            _findings = GameDiagnosticAnalyzer.Analyze(new GameDiagnosticInput
            {
                Metrics = _engine.GetCurrentMetrics(),
                Process = update.State,
                ActiveConnections = cu.Active,
                RecentChanges = _recentChanges.ToList(),
                RecentWindowSeconds = window
            });
        }

        RaiseUpdated();
    }

    /// <summary>An automatic recording ends once the game has stayed closed for <see cref="AutoStopGrace"/>.</summary>
    private bool AutoStopDue(GameRunState state, DateTime now)
    {
        lock (_lock)
        {
            if (!_monitoring || !_autoStarted) return false;
            if (state == GameRunState.Running) { _notRunningSinceUtc = null; return false; }
            _notRunningSinceUtc ??= now;
            return now - _notRunningSinceUtc.Value >= AutoStopGrace;
        }
    }

    private void LogConnectionChanges(IReadOnlyList<ConnectionChange> changes)
    {
        // Only sockets with a visible remote peer on a non-loopback address are worth a timeline entry.
        var notable = changes.Where(c => c.Connection.HasRemote && c.Connection.Scope is AddressScope.Public or AddressScope.LocalNetwork).ToList();
        if (notable.Count == 0) return;

        if (notable.Count <= 5)
        {
            foreach (var c in notable)
                _events.LogEvent("ConnectionChange", "Info", c.Description, _profile!.DisplayName, timestamp: c.AtUtc);
            return;
        }

        int appeared = notable.Count(c => c.Kind == ConnectionChangeKind.Appeared);
        int gone = notable.Count(c => c.Kind == ConnectionChangeKind.Disappeared);
        int state = notable.Count(c => c.Kind == ConnectionChangeKind.StateChanged);
        _events.LogEvent("ConnectionChange", "Info",
            $"{notable.Count} connection changes at once: {appeared} new, {gone} ended, {state} state changes (all recorded in the session).",
            _profile!.DisplayName, timestamp: notable[0].AtUtc);
    }

    private void LogOnce(string key, string type, string severity, string message)
    {
        lock (_lock)
        {
            if (!_reportedWarnings.Add(key)) return;
        }
        _events.LogEvent(type, severity, message);
    }

    // ------------------------------------------------------------------ user actions

    public bool Start(out string message) => StartCore(automatic: false, out message);

    private bool StartCore(bool automatic, out string message)
    {
        MonitoringSession session;
        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            if (_profile == null) { message = "Select a game first."; return false; }
            if (_monitoring) { message = "Already monitoring."; return false; }
            if (_recorder.IsRecording) { message = "A network recording is already running. Stop it on the History & reports page first."; return false; }

            session = _startSession(_profile.Id, $"{_profile.DisplayName} {_clock().ToLocalTime():yyyy-MM-dd HH:mm}");
            _recorder.SetGameContext(_profile);
            _tracker.Clear();
            _recentChanges.Clear();
            _reportedWarnings.Clear();
            _findings = Array.Empty<GameFinding>();
            _startedUtc = session.StartedUtc;
            _monitoring = true;
            _autoStarted = automatic;
            _notRunningSinceUtc = null;

            _events.LogEvent("GameMonitor", "Info", $"Started monitoring for {_profile.DisplayName}{(automatic ? " automatically, because the game started" : "")}. " +
                "Measurements are reference measurements until you choose an endpoint; they are not game-server ping.", _profile.DisplayName, timestamp: _clock());
            message = $"Monitoring {_profile.DisplayName}.";
        }

        RaiseUpdated();
        return true;
    }

    /// <summary>Stops monitoring, stores findings and saves the session. Everything recorded so far is kept even if saving fails.</summary>
    public MonitoringSession? Stop(out string? saveError)
    {
        MonitoringSession? session;
        lock (_lock)
        {
            saveError = null;
            if (!_monitoring) return null;

            var now = _clock();
            _events.LogEvent("GameMonitor", "Info", _autoStarted
                ? $"Stopped monitoring for {_profile?.DisplayName} automatically, because the game was closed."
                : $"Stopped monitoring for {_profile?.DisplayName}.", _profile?.DisplayName ?? "", timestamp: now);

            // final analysis, then close out connections and measured endpoints
            var finalFindings = GameDiagnosticAnalyzer.Analyze(new GameDiagnosticInput
            {
                Metrics = _engine.GetCurrentMetrics(),
                Process = _watcher?.Current,
                ActiveConnections = _tracker.All.Where(c => c.IsActive).ToList(),
                RecentChanges = _recentChanges.ToList(),
                RecentWindowSeconds = _settings().RecentWindowSeconds
            });
            _recorder.SetFindings(finalFindings);

            var closing = _tracker.Update(new ConnectionSnapshot(Array.Empty<RawConnection>(), Array.Empty<string>(), now), new HashSet<int>());
            foreach (var ch in closing.Changes) _recorder.UpsertConnection(ch.Connection);

            _engine.ClearDynamicTargets();
            _measured.Clear();
            _monitoring = false;
            _autoStarted = false;
            _notRunningSinceUtc = null;
            _startedUtc = null;
            _findings = finalFindings;

            session = _stopSession(out saveError);
        }

        RaiseUpdated();
        return session;
    }

    public bool MeasureEndpoint(string connectionId, EndpointMethod method, out string message)
    {
        lock (_lock)
        {
            if (!_monitoring) { message = "Start monitoring before measuring an endpoint."; return false; }
            var conn = _tracker.All.FirstOrDefault(c => c.Id == connectionId);
            if (conn == null) { message = "That connection is no longer tracked."; return false; }

            var target = EndpointMeasurementAdvisor.BuildTarget(conn, method, out var info);
            if (target == null) { message = info; return false; }
            if (!_engine.AddDynamicTarget(target)) { message = "This endpoint is already being measured with that method."; return false; }

            _recorder.RecordTarget(target);
            _measured[target.Id] = (conn.Id, method, _clock(), info, target.Name);
            _events.LogEvent("EndpointMeasurement", "Info",
                $"Started measuring {target.Name}. This endpoint was observed on the game process; its role is unknown and it is not confirmed as the match server.",
                target.Name, timestamp: _clock());
            message = info;
        }

        RaiseUpdated();
        return true;
    }

    public bool StopMeasuring(string targetId)
    {
        lock (_lock)
        {
            if (!_measured.Remove(targetId, out var m)) return false;
            _engine.RemoveDynamicTarget(targetId);
            _events.LogEvent("EndpointMeasurement", "Info", $"Stopped measuring {m.Name}.", m.Name, timestamp: _clock());
        }

        RaiseUpdated();
        return true;
    }

    public bool MarkLag(string? note = null)
    {
        lock (_lock)
        {
            if (!_monitoring) return false;
            var now = _clock();
            _recorder.MarkLag(note, now);
            _events.LogEvent("LagMarker", "Info", string.IsNullOrWhiteSpace(note) ? "You marked a moment of lag." : $"You marked lag: {note.Trim()}", _profile?.DisplayName ?? "", timestamp: now);
        }

        RaiseUpdated();
        return true;
    }

    // ------------------------------------------------------------------ read model

    /// <param name="groupRepeats">
    /// Collapse repeated connections to the same remote endpoint into one row (games reconnect to web services every
    /// minute or two, which otherwise floods the list). The row stands for its most recent connection.
    /// </param>
    public GameMonitorSnapshot Snapshot(bool groupRepeats = true)
    {
        lock (_lock)
        {
            var active = _tracker.All;
            var groups = groupRepeats
                ? active.GroupBy(c => c.Remote == null ? "socket:" + c.Id : $"{c.Protocol}|{c.Remote.Address}|{c.Remote.Port}")
                : active.GroupBy(c => c.Id);

            var views = groups
                .Select(g =>
                {
                    var c = g.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.LastSeenUtc).First();
                    return (Rep: c, Count: g.Count(), First: g.Min(x => x.FirstSeenUtc), Last: g.Max(x => x.LastSeenUtc), Active: g.Any(x => x.IsActive));
                })
                .OrderByDescending(x => x.Active).ThenByDescending(x => x.Rep.HasRemote).ThenByDescending(x => x.Last)
                .Take(300)
                .Select(x =>
                {
                    var c = x.Rep;
                    var options = EndpointMeasurementAdvisor.Advise(c);
                    string hint = string.Join(" | ", options.Select(o => $"{(o.Method == EndpointMethod.Icmp ? "ICMP" : "TCP")}: {(o.IsAvailable ? "available" : "not meaningful - " + o.Explanation)}"));
                    string? measuredBy = c.Remote == null ? null : string.Join(", ", _measured.Where(m => m.Value.ConnectionId == c.Id || EndpointKey(m.Key) == $"{c.Remote.Address}:{c.Remote.Port}")
                        .Select(m => m.Value.Method == EndpointMethod.Icmp ? "ICMP" : "TCP"));
                    var kind = ConnectionKinds.Describe(c);
                    return new ConnectionView(c.Id, c.Protocol.ToString().ToUpperInvariant(), c.LocalDisplay, c.RemoteDisplay, ScopeText(c.Scope), "Unknown role",
                        c.State == ConnectionState.NotApplicable ? "n/a (UDP)" : c.State.ToString(), c.Pid, x.First, x.Last, x.Active, c.HasRemote,
                        string.IsNullOrEmpty(measuredBy) ? null : measuredBy, hint, kind.Kind, kind.Label, kind.Explanation, x.Count);
                }).ToList();

            var activeRemote = active.Where(c => c.IsActive && c.Remote != null && c.Scope == AddressScope.Public)
                .Select(c => $"{c.Protocol}|{c.Remote!.Address}|{c.Remote.Port}").Distinct().Count();

            return new GameMonitorSnapshot
            {
                Profile = _profile,
                Process = _watcher?.Current,
                IsMonitoring = _monitoring,
                StartedUtc = _startedUtc,
                Connections = views,
                DistinctRemoteEndpoints = activeRemote,
                UdpSocketCount = active.Count(c => c.IsActive && c.Protocol == TransportProtocol.Udp),
                WebServiceCount = active.Count(c => c.IsActive && ConnectionKinds.Describe(c).Kind == ConnectionKind.WebService),
                MatchTrafficHidden = _monitoring && _watcher?.Current?.State == GameRunState.Running && active.Any(c => c.IsActive && c.Protocol == TransportProtocol.Udp),
                Measured = _measured.Select(m => new MeasuredEndpointView(m.Key, m.Value.Name, m.Value.Method == EndpointMethod.Icmp ? "ICMP" : "TCP connect",
                    m.Value.Caveat, m.Value.StartedUtc, active.Any(c => c.IsActive && c.Id == m.Value.ConnectionId))).ToList(),
                Findings = _findings,
                Warnings = _warnings
            };
        }
    }

    private static string EndpointKey(string targetId)
    {
        // "game:Method:ip:port" -> "ip:port"
        var parts = targetId.Split(':', 3);
        return parts.Length == 3 ? parts[2] : targetId;
    }

    private static string ScopeText(AddressScope s) => s switch
    {
        AddressScope.Loopback => "This PC (loopback)",
        AddressScope.LocalNetwork => "Local network",
        AddressScope.Public => "Internet",
        AddressScope.Multicast => "Multicast/broadcast",
        _ => "Unspecified"
    };

    private void RaiseUpdated() => Updated?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ shutdown

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        // stop and save any recording first so a shutdown during gameplay keeps everything recorded so far
        Stop(out _);

        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            _disposed = true;
            (cts, task) = (_loopCts, _loopTask);
            (_loopCts, _loopTask) = (null, null);
        }
        StopLoop(cts, task);
    }
}
