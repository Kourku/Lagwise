using System;
using System.Collections.Generic;
using System.Linq;

namespace Lagwise.Core.Games;

public enum ProcessRole
{
    /// <summary>The process that actually runs the game (match/world). Only this role means "the game is running".</summary>
    Game,
    /// <summary>Launcher, store client or game-client shell. Running does not mean the game is being played.</summary>
    Launcher,
    AntiCheat,
    Helper,
    CrashReporter
}

/// <summary>Where Lagwise learned about a game.</summary>
public enum GameSource
{
    /// <summary>One of the games Lagwise knows by name (it can tell their launchers from the game itself).</summary>
    BuiltIn,
    /// <summary>A game Windows recorded on this PC (the list Windows' Game Bar keeps of the games it has seen run).</summary>
    Windows,
    /// <summary>A program the user chose as a game.</summary>
    AddedByYou
}

/// <param name="ExecutableName">File name without ".exe", compared case-insensitively.</param>
/// <param name="PathHints">
/// Optional folder-name fragments. If the executable path is readable and contains none of them, the match is
/// reported as uncertain rather than silently treated as this game.
/// </param>
/// <param name="ExactPath">
/// The game's own file on this PC (games from Windows' list or added by the user). A process of that name elsewhere is
/// not this game, and one whose path cannot be read is not claimed either.
/// </param>
public sealed record ProcessRule(string ExecutableName, ProcessRole Role, string[]? PathHints = null, string? Note = null, string? ExactPath = null);

/// <param name="IsOfficial">True only for a page published by the game's publisher/operator.</param>
public sealed record StatusResource(string? Url, bool IsOfficial, string Note)
{
    public static StatusResource None { get; } = new(null, false, "");
}

/// <summary>
/// A game Lagwise can recognise while it runs. Nothing here claims anything about the game's servers: Lagwise measures
/// the player's own connection while the game runs, the same way for every game.
/// </summary>
public sealed record GameProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public required IReadOnlyList<ProcessRule> ProcessRules { get; init; }
    public StatusResource StatusPage { get; init; } = StatusResource.None;
    public GameSource Source { get; init; } = GameSource.BuiltIn;
    /// <summary>The game's main program on this PC, when known (from Windows' list, the user, or a running process).</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>When Windows last saw the game run, from its game list; null when unknown.</summary>
    public DateTime? LastPlayedUtc { get; init; }

    public IEnumerable<ProcessRule> RulesFor(ProcessRole role) => ProcessRules.Where(r => r.Role == role);

    public IReadOnlyCollection<string> AllProcessNames =>
        ProcessRules.Select(r => r.ExecutableName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasStatusPage => !string.IsNullOrEmpty(StatusPage.Url);
}
