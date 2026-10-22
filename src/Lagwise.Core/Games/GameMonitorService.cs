using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Core.Games;

public delegate MonitoringSession? StopSessionDelegate(out string? error);

public sealed class GameMonitorSnapshot
{
    /// <summary><see cref="GameMonitorService.AllGames"/>, one game's id, or null when nothing is watched.</summary>
    public string? Watching { get; init; }
    public IReadOnlyList<GameProfile> Library { get; init; } = Array.Empty<GameProfile>();
    /// <summary>The game being recorded; else the game running now; else the one game watched; null otherwise.</summary>
    public GameProfile? Game { get; init; }
    /// <summary>What is running of <see cref="Game"/>.</summary>
    public GameProcessState? Process { get; init; }
    /// <summary>Every watched game running right now.</summary>
    public IReadOnlyList<GameProfile> Running { get; init; } = Array.Empty<GameProfile>();
    public bool IsMonitoring { get; init; }
    public bool StartedAutomatically { get; init; }
    public DateTime? StartedUtc { get; init; }
    /// <summary>The recorded game's UDP sockets (where games send the match itself) and internet connections now.</summary>
    public int UdpSockets { get; init; }
    public int InternetConnections { get; init; }
}

/// <summary>
/// Orchestrates the Game Monitor: watches for the user's games (all of them, or one), records how the connection behaves
/// while one runs (automatically if asked), and keeps the game's processes and sockets in the recording (whether it was
/// online). Process polling is one light background loop; sockets are only read while a recorded game runs.
/// </summary>
public sealed class GameMonitorService : IDisposable
{
    /// <summary>Watch every game in the library.</summary>
    public const string AllGames = "*";

    private readonly object _lock = new();
    private readonly SessionRecorder _recorder;
    private readonly EventLogService _events;
    private readonly Func<AppSettings> _settings;
    private readonly IProcessSource _processes;
    private readonly IConnectionSource _connections;
    private readonly Func<string?, string?, MonitoringSession> _startSession;
    private readonly StopSessionDelegate _stopSession;
    private readonly Func<DateTime> _clock;

    private readonly ConnectionTracker _tracker = new();
    private readonly HashSet<string> _reportedWarnings = new();
    private readonly Dictionary<string, GameWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<GameProfile> _library;
    private string? _selection;
    private GameProfile? _recorded;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _monitoring;
    private bool _autoStarted;
    private DateTime? _notRunningSinceUtc;
    private DateTime? _startedUtc;
    private bool _disposed;
    /// <summary>A game whose recording the user stopped while it ran: not recorded again by itself until it has closed.</summary>
    private string? _noAutoFor;

    /// <summary>Poll period while recording (process list + socket tables).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Poll period while only watching (process list only).</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// With automatic recording, how long the game must stay closed before the recording is stopped and saved.
    /// Bridges quick restarts and the gap between a game's processes (for example League's loading screen).
    /// </summary>
    public TimeSpan AutoStopGrace { get; set; } = TimeSpan.FromSeconds(20);
    public event EventHandler? Updated;
    /// <summary>
    /// A watched game was started while Lagwise watched (not one already running as watching began). Raised from the
    /// polling thread, whether or not a recording runs.
    /// </summary>
    public event EventHandler<GameProfile>? GameLaunched;

    public GameMonitorService(
        SessionRecorder recorder, EventLogService events, Func<AppSettings> settings,
        IProcessSource processes, IConnectionSource connections,
        Func<string?, string?, MonitoringSession> startSession, StopSessionDelegate stopSession, Func<DateTime>? clock = null,
        IReadOnlyList<GameProfile>? library = null)
    {
        _library = library ?? GameProfiles.All;
        _recorder = recorder;
        _events = events;
        _settings = settings;
        _processes = processes;
        _connections = connections;
        _startSession = startSession;
        _stopSession = stopSession;
        _clock = clock ?? (() => DateTime.UtcNow);
        (_processes as SystemProcessSource)?.SetInterestingNames(_library.SelectMany(p => p.AllProcessNames));
    }

    public bool IsMonitoring { get { lock (_lock) return _monitoring; } }
    /// <summary>True when the current recording was started by automatic recording (and will stop when the game closes).</summary>
    public bool StartedAutomatically { get { lock (_lock) return _monitoring && _autoStarted; } }
    /// <summary><see cref="AllGames"/>, a game's id, or null.</summary>
    public string? Watching { get { lock (_lock) return _selection; } }
    public IReadOnlyList<GameProfile> Library { get { lock (_lock) return _library; } }
    /// <summary>The game the current recording is about (null while it waits for one, and when not recording).</summary>
    public GameProfile? RecordedGame { get { lock (_lock) return _monitoring ? _recorded : null; } }

    // ------------------------------------------------------------------ library / selection / loop

    /// <summary>Replaces the games Lagwise knows (found again at start-up, or a game was added or removed).</summary>
    public void SetLibrary(IReadOnlyList<GameProfile> library)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _library = library;
            (_processes as SystemProcessSource)?.SetInterestingNames(library.SelectMany(p => p.AllProcessNames));
            RebuildWatchers();
        }
        RaiseUpdated();
    }

    /// <summary>Chooses what to watch (<see cref="AllGames"/> or one game's id; null for nothing). Not allowed while recording.</summary>
    public bool Select(string? id, out string message)
    {
        CancellationTokenSource? oldCts;
        Task? oldTask;
        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            if (_monitoring) { message = "Stop recording before changing the game."; return false; }
            (oldCts, oldTask) = (_loopCts, _loopTask);
            (_loopCts, _loopTask) = (null, null);
        }

        // wait for the previous loop outside the lock: the loop itself takes the lock
        StopLoop(oldCts, oldTask);

        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            _selection = id == AllGames ? AllGames : _library.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Id;
            _watchers.Clear();
            _tracker.Clear();
            RebuildWatchers();
            if (_selection != null)
            {
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _loopTask = Task.Run(() => RunLoopAsync(token));
            }
            message = _selection == null ? "No game selected."
                : _selection == AllGames ? "Watching for all your games."
                : $"Watching for {_library.First(p => p.Id == _selection).DisplayName}.";
        }

        RaiseUpdated();
        return true;
    }

    /// <summary>Keeps one watcher per watched game; a watcher that exists keeps its state (so nothing "starts" twice).</summary>
    private void RebuildWatchers()
    {
        var wanted = _selection == null ? new List<GameProfile>()
            : _selection == AllGames ? _library.ToList()
            : _library.Where(p => p.Id == _selection).ToList();
        if (_recorded != null && _monitoring && wanted.All(p => p.Id != _recorded.Id)) wanted.Add(_recorded);
        var ids = wanted.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _watchers.Keys.Where(k => !ids.Contains(k)).ToList()) _watchers.Remove(gone);
        foreach (var p in wanted) if (!_watchers.ContainsKey(p.Id)) _watchers[p.Id] = new GameWatcher(p);
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
                    LogOnce("poll-error:" + ex.GetType().Name, "GameMonitor", "Warning", $"Watching for games failed once: {ex.Message}");
                }

                // detect games cheaply and slowly while idle; poll faster only while recording
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
    /// stop countdown and logging a false restart.
    /// </summary>
    private readonly object _pollLock = new();

    public void PollOnce()
    {
        lock (_pollLock) PollOnceCore();
    }

    private void PollOnceCore()
    {
        List<GameWatcher> watchers;
        lock (_lock)
        {
            watchers = _watchers.Values.ToList();
        }
        if (watchers.Count == 0) return;

        var now = _clock();
        var processes = _processes.GetProcesses();      // the slow part, outside the lock

        var updates = new Dictionary<string, WatchUpdate>(StringComparer.OrdinalIgnoreCase);
        var launched = new List<GameProfile>();
        lock (_lock)
        {
            foreach (var w in watchers)
            {
                if (!_watchers.TryGetValue(w.Profile.Id, out var current) || current != w) continue;   // selection changed meanwhile
                var update = w.Observe(processes, now);
                updates[w.Profile.Id] = update;
                if (w.Observations > 1 && update.Changes.Any(c => c.Kind == GameProcessChangeKind.GameStarted)) launched.Add(w.Profile);
            }
        }
        foreach (var p in launched) GameLaunched?.Invoke(this, p);

        var running = updates.Values.Where(u => u.State.State == GameRunState.Running).ToList();
        bool monitoring;
        lock (_lock)
        {
            monitoring = _monitoring;
            if (_noAutoFor != null && running.All(r => r.State.Profile.Id != _noAutoFor)) _noAutoFor = null;   // it closed
        }
        var candidates = running.Where(r => r.State.Profile.Id != _noAutoFor).ToList();

        if (!monitoring && candidates.Count > 0 && _settings().AutoRecordGame && !_recorder.IsRecording)
        {
            monitoring = StartCore(automatic: true, Pick(candidates, launched).State.Profile, out _);
        }
        else if (monitoring && AutoStopDue(updates, now))
        {
            StopCore(byUser: false, out _);
            return;
        }

        if (!monitoring)
        {
            RaiseUpdated();
            return;
        }

        // a recording that waited for a game gets the first one that starts
        lock (_lock)
        {
            if (_monitoring && _recorded == null && running.Count > 0)
            {
                _recorded = Pick(running, launched).State.Profile;
                _recorder.SetGameContext(_recorded);
                RebuildWatchers();
            }
        }

        WatchUpdate? mine;
        lock (_lock) mine = _recorded != null && updates.TryGetValue(_recorded.Id, out var u) ? u : null;
        bool gameRunning = mine?.State.State == GameRunState.Running;
        var snapshot = gameRunning ? _connections.Capture() : new ConnectionSnapshot(Array.Empty<RawConnection>(), Array.Empty<string>(), now);

        lock (_lock)
        {
            if (!_monitoring) return;
            if (mine != null && _recorded != null)
            {
                foreach (var change in mine.Changes)
                    _events.LogEvent("GameProcess", change.Kind == GameProcessChangeKind.GameExited ? "Warning" : "Info", change.Description, _recorded.DisplayName, timestamp: change.AtUtc);
                foreach (var p in mine.State.GameProcesses) _recorder.UpsertGameProcess(p, now);
                _recorder.MarkGameProcessesExited(mine.State.GameProcesses.Select(p => p.Identity).ToHashSet(), now);

                var cu = _tracker.Update(snapshot with { CapturedUtc = now }, mine.State.GamePids);
                foreach (var c in _tracker.All.Where(c => c.IsActive).Concat(cu.Changes.Select(ch => ch.Connection)).DistinctBy(c => c.Id))
                    _recorder.UpsertConnection(c);
                foreach (var w in cu.Warnings)
                    LogOnce("conn-warning:" + w, "GameMonitor", "Info", "Some of the game's connections could not be read: " + w);
            }
        }

        RaiseUpdated();
    }

    /// <summary>The game that just started, else the one that has run longest.</summary>
    private static WatchUpdate Pick(List<WatchUpdate> running, List<GameProfile> launched) =>
        running.FirstOrDefault(r => launched.Any(l => l.Id == r.State.Profile.Id))
        ?? running.OrderBy(r => r.State.Primary?.StartTimeUtc ?? DateTime.MaxValue).First();

    /// <summary>An automatic recording ends once its game has stayed closed for <see cref="AutoStopGrace"/>.</summary>
    private bool AutoStopDue(Dictionary<string, WatchUpdate> updates, DateTime now)
    {
        lock (_lock)
        {
            if (!_monitoring || !_autoStarted || _recorded == null) return false;
            bool running = updates.TryGetValue(_recorded.Id, out var u) && u.State.State == GameRunState.Running;
            if (running) { _notRunningSinceUtc = null; return false; }
            _notRunningSinceUtc ??= now;
            return now - _notRunningSinceUtc.Value >= AutoStopGrace;
        }
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

    /// <summary>
    /// Starts recording by hand: about the game running now (or the one game watched); with all games watched and none
    /// running, the recording waits and takes the first game that starts.
    /// </summary>
    public bool Start(out string message)
    {
        GameProfile? game;
        lock (_lock)
        {
            game = _watchers.Values.Select(w => w.Current).Where(s => s?.State == GameRunState.Running).Select(s => s!.Profile).FirstOrDefault()
                   ?? (_selection is { } sel && sel != AllGames ? _library.FirstOrDefault(p => p.Id == sel) : null);
        }
        return StartCore(automatic: false, game, out message);
    }

    private bool StartCore(bool automatic, GameProfile? game, out string message)
    {
        lock (_lock)
        {
            if (_disposed) { message = "The monitor has been shut down."; return false; }
            if (_selection == null) { message = "Choose a game first."; return false; }
            if (_monitoring) { message = "Already recording."; return false; }
            if (_recorder.IsRecording) { message = "A network recording is already running. Stop it on the History & reports page first."; return false; }

            string when = _clock().ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            _startSession(game?.Id, game != null ? $"{game.DisplayName} {when}" : $"{SessionRecorder.WaitingName} {when}");
            _recorder.MarkGameRecording();
            if (game != null) _recorder.SetGameContext(game);
            if (automatic) _recorder.MarkAutomatic();
            _recorded = game;
            _tracker.Clear();
            _reportedWarnings.Clear();
            _startedUtc = _recorder.StartedUtc ?? _clock();
            _monitoring = true;
            _autoStarted = automatic;
            _notRunningSinceUtc = null;
            RebuildWatchers();

            _events.LogEvent("GameMonitor", "Info", game == null
                ? "Started recording. It is about the first of your games that starts."
                : $"Started recording {game.DisplayName}{(automatic ? " automatically, because it started" : "")}.", game?.DisplayName ?? "", timestamp: _clock());
            message = game == null ? "Recording. Start your game whenever you are ready." : $"Recording {game.DisplayName}.";
        }

        RaiseUpdated();
        return true;
    }

    /// <summary>
    /// Stops recording and saves the session. Everything recorded so far is kept even if saving fails. Stopped by hand
    /// while the game still runs, automatic recording leaves that game alone until it has closed.
    /// </summary>
    public MonitoringSession? Stop(out string? saveError) => StopCore(byUser: true, out saveError);

    /// <summary>The game whose recording was stopped by hand while it ran (not recorded again by itself until it closes).</summary>
    public string? NotRecordingAgain { get { lock (_lock) return _noAutoFor; } }

    private MonitoringSession? StopCore(bool byUser, out string? saveError)
    {
        MonitoringSession? session;
        lock (_lock)
        {
            saveError = null;
            if (!_monitoring) return null;
            if (byUser && _recorded != null && _watchers.TryGetValue(_recorded.Id, out var w) && w.Current?.State == GameRunState.Running)
                _noAutoFor = _recorded.Id;

            var now = _clock();
            string name = _recorded?.DisplayName ?? "the game";
            _events.LogEvent("GameMonitor", "Info", _autoStarted
                ? $"Stopped recording {name} automatically, because it was closed."
                : $"Stopped recording {name}.", _recorded?.DisplayName ?? "", timestamp: now);

            var closing = _tracker.Update(new ConnectionSnapshot(Array.Empty<RawConnection>(), Array.Empty<string>(), now), new HashSet<int>());
            foreach (var ch in closing.Changes) _recorder.UpsertConnection(ch.Connection);

            _monitoring = false;
            _autoStarted = false;
            _notRunningSinceUtc = null;
            _startedUtc = null;
            _recorded = null;
            RebuildWatchers();

            session = _stopSession(out saveError);
        }

        RaiseUpdated();
        return session;
    }

    public bool MarkLag(string? note = null)
    {
        lock (_lock)
        {
            if (!_monitoring) return false;
            var now = _clock();
            _recorder.MarkLag(note, now);
            _events.LogEvent("LagMarker", "Info", string.IsNullOrWhiteSpace(note) ? "You marked a moment of lag." : $"You marked lag: {note.Trim()}", _recorded?.DisplayName ?? "", timestamp: now);
        }

        RaiseUpdated();
        return true;
    }

    // ------------------------------------------------------------------ read model

    public GameMonitorSnapshot Snapshot()
    {
        lock (_lock)
        {
            var running = _watchers.Values.Where(w => w.Current?.State == GameRunState.Running).Select(w => w.Profile).ToList();
            var game = (_monitoring ? _recorded : null)
                       ?? running.FirstOrDefault()
                       ?? (_selection is { } sel && sel != AllGames ? _library.FirstOrDefault(p => p.Id == sel) : null);
            var active = _tracker.All.Where(c => c.IsActive).ToList();
            return new GameMonitorSnapshot
            {
                Watching = _selection,
                Library = _library,
                Game = game,
                Process = game != null && _watchers.TryGetValue(game.Id, out var w) ? w.Current : null,
                Running = running,
                IsMonitoring = _monitoring,
                StartedAutomatically = _monitoring && _autoStarted,
                StartedUtc = _startedUtc,
                UdpSockets = _monitoring ? active.Count(c => c.Protocol == TransportProtocol.Udp) : 0,
                InternetConnections = _monitoring ? active.Count(c => c.Remote != null && c.Scope == AddressScope.Public) : 0
            };
        }
    }

    private void RaiseUpdated() => Updated?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ shutdown

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        // stop and save any recording first so a shutdown during gameplay keeps everything recorded so far
        StopCore(byUser: false, out _);

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
