using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPulse.Core.Games;

public enum GameProcessChangeKind
{
    GameStarted,
    GameExited,
    /// <summary>The set of game process instances changed while the game stayed running (restart, new child, PID reuse).</summary>
    InstancesChanged
}

public sealed record GameProcessChange(GameProcessChangeKind Kind, string Description, DateTime AtUtc);

public sealed record WatchUpdate(GameProcessState State, IReadOnlyList<GameProcessChange> Changes);

/// <summary>
/// Pure state machine that turns successive process snapshots into start/exit/restart transitions.
/// Instances are tracked by (PID, start time), so a recycled PID is treated as a new process.
/// </summary>
public sealed class GameWatcher
{
    private HashSet<ProcessIdentity> _known = new();
    private bool _wasRunning;

    public GameProfile Profile { get; }
    public GameProcessState? Current { get; private set; }
    /// <summary>Number of times the game has gone from not running to running since this watcher was created.</summary>
    public int LaunchCount { get; private set; }

    public GameWatcher(GameProfile profile)
    {
        Profile = profile;
    }

    public WatchUpdate Observe(IEnumerable<ProcessInfo> processes, DateTime nowUtc)
    {
        var state = GameProcessDetector.Detect(Profile, processes);
        var changes = new List<GameProcessChange>();
        var identities = state.GameProcesses.Select(p => p.Identity).ToHashSet();
        bool running = state.State == GameRunState.Running;

        if (running && !_wasRunning)
        {
            LaunchCount++;
            changes.Add(new(GameProcessChangeKind.GameStarted,
                $"{Profile.DisplayName} detected: {Describe(state)} (confidence {state.Confidence}).", nowUtc));
        }
        else if (!running && _wasRunning)
        {
            changes.Add(new(GameProcessChangeKind.GameExited,
                $"{Profile.DisplayName} is no longer running.", nowUtc));
        }
        else if (running && !identities.SetEquals(_known))
        {
            var added = identities.Except(_known).ToList();
            var removed = _known.Except(identities).ToList();
            changes.Add(new(GameProcessChangeKind.InstancesChanged,
                $"{Profile.DisplayName} process set changed (+{added.Count} / -{removed.Count}): now {Describe(state)}.", nowUtc));
        }

        _known = identities;
        _wasRunning = running;
        Current = state;
        return new WatchUpdate(state, changes);
    }

    private static string Describe(GameProcessState s) =>
        string.Join(", ", s.GameProcesses.Select(p => $"{p.Name} PID {p.Pid}"));
}
