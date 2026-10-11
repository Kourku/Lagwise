using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPulse.Core.Games;

/// <summary>
/// Data-driven catalog of the seven target games. All game-specific knowledge lives here, not in the monitoring code.
/// Rules for adding/changing a profile: never invent endpoints or status URLs; mark process names as verified only
/// when confirmed on a real installation; never set LiveValidated without live evidence.
/// </summary>
public static class GameProfiles
{
    private const string UdpLimitation =
        "Most real-time game traffic uses UDP. Windows only reports the local port a process has bound for UDP, so the remote peers of that traffic cannot be seen without elevated packet tracing (not implemented). Remote endpoints shown come from TCP connections.";
    private const string RoleLimitation =
        "A game also talks to login, matchmaking, telemetry, voice and content services. Observed endpoints are labelled 'Unknown role'; none is assumed to be the match server.";
    private const string NoStaticServer =
        "Match servers are assigned dynamically, so there is no fixed server address NetPulse can verify in advance.";

    private static readonly GameCapabilities ObservationOnly = new(
        ProcessDetection: true,
        ConnectionDiscovery: true,
        ReferenceMeasurement: true,
        EndpointMeasurement: true,
        MatchServerIdentification: false);

    public static IReadOnlyList<GameProfile> All { get; } = new List<GameProfile>
    {
        new()
        {
            Id = "lol",
            DisplayName = "League of Legends",
            Publisher = "Riot Games",
            ProcessNamesVerified = true,
            DetectionConfidence = DetectionConfidence.High,
            DetectionMethods = new[] { "Running process name (match executable), optional install-folder check" },
            ProcessRules = new ProcessRule[]
            {
                new("League of Legends", ProcessRole.Game, new[] { "League of Legends" }, "In-match game process (Game\\League of Legends.exe)"),
                new("LeagueClient", ProcessRole.Launcher, new[] { "League of Legends" }, "Client shell (lobby/queue); running does not mean a match is in progress"),
                new("LeagueClientUx", ProcessRole.Launcher, new[] { "League of Legends" }),
                new("LeagueClientUxRender", ProcessRole.Helper, new[] { "League of Legends" }),
                new("RiotClientServices", ProcessRole.Launcher, new[] { "Riot Client" }),
                new("Riot Client", ProcessRole.Launcher, new[] { "Riot Client" }),
                new("LeagueCrashHandler64", ProcessRole.CrashReporter),
                new("RiotClientCrashHandler", ProcessRole.CrashReporter)
            },
            Capabilities = ObservationOnly,
            StatusPage = new("https://status.riotgames.com/", true, "Official Riot Games service status."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation },
            Validation = ValidationLevel.SyntheticTests,
            ValidationNotes = "Executable names confirmed against an installation on the development PC. Detection of the launcher-only state was exercised against the real Riot Client. "
                + "Not yet validated during a real match (game process + live connections), so the profile stays Reference-only."
        },
        new()
        {
            Id = "fortnite",
            DisplayName = "Fortnite",
            Publisher = "Epic Games",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Medium,
            ProcessRules = new ProcessRule[]
            {
                new("FortniteClient-Win64-Shipping", ProcessRole.Game),
                new("FortniteLauncher", ProcessRole.Launcher),
                new("EpicGamesLauncher", ProcessRole.Launcher),
                new("FortniteClient-Win64-Shipping_BE", ProcessRole.AntiCheat, null, "BattlEye stub"),
                new("FortniteClient-Win64-Shipping_EAC", ProcessRole.AntiCheat, null, "Easy Anti-Cheat stub"),
                new("EasyAntiCheat_EOS", ProcessRole.AntiCheat),
                new("BEService", ProcessRole.AntiCheat)
            },
            Capabilities = ObservationOnly,
            StatusPage = new("https://status.epicgames.com/", true, "Epic Games public status page (covers Fortnite services)."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "Process names come from documented knowledge and have not been confirmed on a real installation." }
        },
        new()
        {
            Id = "apex",
            DisplayName = "Apex Legends",
            Publisher = "Electronic Arts / Respawn",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Medium,
            ProcessRules = new ProcessRule[]
            {
                new("r5apex", ProcessRole.Game),
                new("r5apex_dx12", ProcessRole.Game),
                new("EADesktop", ProcessRole.Launcher),
                new("EABackgroundService", ProcessRole.Helper),
                new("EasyAntiCheat_EOS", ProcessRole.AntiCheat)
            },
            Capabilities = ObservationOnly,
            StatusPage = new("https://help.ea.com/en/server-status/", true, "Official EA server status page."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "Process names come from documented knowledge and have not been confirmed on a real installation." }
        },
        new()
        {
            Id = "cs2",
            DisplayName = "Counter-Strike 2",
            Publisher = "Valve",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Medium,
            ProcessRules = new ProcessRule[]
            {
                new("cs2", ProcessRole.Game),
                new("steam", ProcessRole.Launcher),
                new("steamwebhelper", ProcessRole.Helper)
            },
            Capabilities = ObservationOnly,
            StatusPage = new(null, false, "Valve does not publish an official status page for Counter-Strike 2 (checked 2026-10-10). Third-party trackers exist but are not endorsed here."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "Process names come from documented knowledge and have not been confirmed on a real installation." }
        },
        new()
        {
            Id = "wow",
            DisplayName = "World of Warcraft",
            Publisher = "Blizzard Entertainment",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Medium,
            ProcessRules = new ProcessRule[]
            {
                new("Wow", ProcessRole.Game, new[] { "World of Warcraft" }),
                new("WowClassic", ProcessRole.Game, new[] { "World of Warcraft" }),
                new("Wow-64", ProcessRole.Game, new[] { "World of Warcraft" }),
                new("Battle.net", ProcessRole.Launcher),
                new("Agent", ProcessRole.Helper, new[] { "Battle.net" }, "Blizzard update agent"),
                new("BlizzardError", ProcessRole.CrashReporter)
            },
            Capabilities = ObservationOnly,
            StatusPage = new(null, false, "Blizzard publishes realm status on its website and in the game client, but no stable status URL was verified (checked 2026-10-10)."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "World of Warcraft mostly uses TCP, so remote endpoints are more likely to be visible than in UDP games, but a realm connection is not distinguishable from other services without more evidence.", "Process names come from documented knowledge and have not been confirmed on a real installation." }
        },
        new()
        {
            Id = "marvelrivals",
            DisplayName = "Marvel Rivals",
            Publisher = "NetEase Games",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Low,
            ProcessRules = new ProcessRule[]
            {
                new("Marvel-Win64-Shipping", ProcessRole.Game),
                new("MarvelRivals_Launcher", ProcessRole.Launcher)
            },
            Capabilities = ObservationOnly,
            StatusPage = new(null, false, "No official server-status page was found; NetEase announces maintenance on its official social channels (checked 2026-10-10)."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "Process names are the least certain of the seven profiles and have not been confirmed on a real installation; detection may fail." }
        },
        new()
        {
            Id = "poe",
            DisplayName = "Path of Exile",
            Publisher = "Grinding Gear Games",
            ProcessNamesVerified = false,
            DetectionConfidence = DetectionConfidence.Medium,
            ProcessRules = new ProcessRule[]
            {
                new("PathOfExile", ProcessRole.Game),
                new("PathOfExile_x64", ProcessRole.Game),
                new("PathOfExileSteam", ProcessRole.Game),
                new("PathOfExile_x64Steam", ProcessRole.Game),
                new("PathOfExile_x64EGS", ProcessRole.Game),
                new("PathOfExile2", ProcessRole.Game, null, "Path of Exile 2 shares this profile"),
                new("PathOfExileLauncher", ProcessRole.Launcher)
            },
            Capabilities = ObservationOnly,
            StatusPage = new(null, false, "No official status page was verified; Grinding Gear Games announces outages on its forums and official social accounts (checked 2026-10-10)."),
            Limitations = new[] { NoStaticServer, UdpLimitation, RoleLimitation, "Process names come from documented knowledge and have not been confirmed on a real installation." }
        }
    };

    public static GameProfile? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every executable name referenced by any profile (used to limit expensive per-process lookups).</summary>
    public static IReadOnlyCollection<string> AllProcessNames { get; } =
        All.SelectMany(p => p.AllProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
