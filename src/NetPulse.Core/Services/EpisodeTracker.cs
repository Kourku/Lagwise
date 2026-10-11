using System;
using System.Collections.Generic;

namespace NetPulse.Core.Services;

public enum EpisodeTransition
{
    None,
    Started,
    Ended
}

/// <summary>
/// Turns a noisy per-sample condition into start/end transitions so that a persisting problem is reported once
/// when it begins and once when it clears, rather than on every sample.
/// </summary>
public class EpisodeTracker
{
    private readonly Dictionary<string, DateTime> _active = new();

    public bool IsActive(string key) => _active.ContainsKey(key);

    public EpisodeTransition Update(string key, bool conditionMet, DateTime now, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        bool active = _active.TryGetValue(key, out var started);

        if (conditionMet && !active)
        {
            _active[key] = now;
            return EpisodeTransition.Started;
        }

        if (!conditionMet && active)
        {
            _active.Remove(key);
            duration = now - started;
            return EpisodeTransition.Ended;
        }

        return EpisodeTransition.None;
    }

    public void Reset() => _active.Clear();

    public void Remove(string key) => _active.Remove(key);
}
