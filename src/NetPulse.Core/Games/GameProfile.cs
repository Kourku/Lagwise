using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPulse.Core.Games;

/// <summary>
/// How far a game integration can be trusted. Derived from validation evidence, never set by hand:
/// a game is only Supported once its capabilities were validated against the real client.
/// </summary>
public enum SupportStatus
{
    /// <summary>Advertised capabilities are implemented AND validated against the real game client.</summary>
    Supported,
    /// <summary>Generic network measurements and process/connection observation work, but actual game-server monitoring is not validated.</summary>
    ReferenceOnly,
    /// <summary>The integration is incomplete (for example no process detection).</summary>
    NotImplemented
}

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

public enum DetectionConfidence { High, Medium, Low }

public enum ValidationLevel
{
    /// <summary>Nothing checked beyond the profile data.</summary>
    None,
    /// <summary>Verified with simulated processes/connections in automated tests only.</summary>
    SyntheticTests,
    /// <summary>Verified against the real client and real network connections.</summary>
    LiveValidated
}

/// <param name="ExecutableName">File name without ".exe", compared case-insensitively.</param>
/// <param name="PathHints">
/// Optional folder-name fragments. If the executable path is readable and contains none of them, the match is
/// reported as uncertain rather than silently treated as this game.
/// </param>
public sealed record ProcessRule(string ExecutableName, ProcessRole Role, string[]? PathHints = null, string? Note = null);

/// <param name="IsOfficial">True only for a page published by the game's publisher/operator.</param>
public sealed record StatusResource(string? Url, bool IsOfficial, string Note);

/// <summary>An endpoint that was verified (by the publisher or by measurement) as a stable diagnostic target for this game.</summary>
public sealed record VerifiedEndpoint(string Host, string Description, string Source);

public sealed record GameCapabilities(
    bool ProcessDetection,
    bool ConnectionDiscovery,
    bool ReferenceMeasurement,
    bool EndpointMeasurement,
    bool MatchServerIdentification);

public sealed class GameProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Publisher { get; init; }
    public required IReadOnlyList<ProcessRule> ProcessRules { get; init; }
    /// <summary>True when the executable names were confirmed against a real installation (not just documented knowledge).</summary>
    public bool ProcessNamesVerified { get; init; }
    public DetectionConfidence DetectionConfidence { get; init; } = DetectionConfidence.Medium;
    public IReadOnlyList<string> DetectionMethods { get; init; } = new[] { "Running process name" };
    /// <summary>Empty unless an endpoint has genuinely been verified. Match servers are dynamic and are never listed here.</summary>
    public IReadOnlyList<VerifiedEndpoint> VerifiedEndpoints { get; init; } = Array.Empty<VerifiedEndpoint>();
    public required GameCapabilities Capabilities { get; init; }
    public required StatusResource StatusPage { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
    public ValidationLevel Validation { get; init; } = ValidationLevel.SyntheticTests;
    public string ValidationNotes { get; init; } = string.Empty;

    public SupportStatus Status =>
        !Capabilities.ProcessDetection || ProcessRules.All(r => r.Role != ProcessRole.Game)
            ? SupportStatus.NotImplemented
            : Validation == ValidationLevel.LiveValidated
                ? SupportStatus.Supported
                : SupportStatus.ReferenceOnly;

    public string StatusExplanation => Status switch
    {
        SupportStatus.Supported => "Process detection and connection discovery were validated against the real client.",
        SupportStatus.ReferenceOnly => "Process and connection observation are implemented, and generic reference measurements work. "
            + "Match-server monitoring is not available or not yet validated against the real game, so results are reference measurements, not game-server ping.",
        _ => "This integration is incomplete."
    };

    public IEnumerable<ProcessRule> RulesFor(ProcessRole role) => ProcessRules.Where(r => r.Role == role);

    public IReadOnlyCollection<string> AllProcessNames =>
        ProcessRules.Select(r => r.ExecutableName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
