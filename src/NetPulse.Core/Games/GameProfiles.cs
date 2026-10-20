using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPulse.Core.Games;

/// <summary>
/// The games NetPulse knows by name. Every other game is found in Windows' own game list or added by the user
/// (see <see cref="GameLibrary"/>), so this list only adds what a name alone gives: a proper title, telling a game's own
/// client or launcher apart from the game itself, and an official status page.
/// Rules: never invent endpoints or status URLs. A store client shared by many games (Steam, Epic Games Launcher,
/// EA app, Battle.net) is not listed for any one game, because it runs whether or not that game is played.
/// Executable names confirmed on a real PC (Windows' game list of the development PC, 2026-10-20): League of Legends,
/// Fortnite, Apex Legends (r5apex_dx12), Counter-Strike 2 and Marvel Rivals; League's client files were also read
/// from its install folder. World of Warcraft and Path of Exile names come from documentation; if they are wrong, the
/// game is still found through Windows' list.
/// </summary>
public static class GameProfiles
{
    public static IReadOnlyList<GameProfile> All { get; } = new List<GameProfile>
    {
        new()
        {
            Id = "lol",
            DisplayName = "League of Legends",
            Publisher = "Riot Games",
            ProcessRules = new ProcessRule[]
            {
                new("League of Legends", ProcessRole.Game, new[] { "League of Legends" }, "In-match game process (Game\\League of Legends.exe)"),
                new("LeagueClient", ProcessRole.Launcher, new[] { "League of Legends" }, "Client (lobby and queue): running does not mean a match is being played"),
                new("LeagueClientUx", ProcessRole.Launcher, new[] { "League of Legends" }),
                new("LeagueClientUxRender", ProcessRole.Helper, new[] { "League of Legends" }),
                new("LeagueCrashHandler64", ProcessRole.CrashReporter)
            },
            StatusPage = new("https://status.riotgames.com/", true, "Riot Games service status")
        },
        new()
        {
            Id = "fortnite",
            DisplayName = "Fortnite",
            Publisher = "Epic Games",
            ProcessRules = new ProcessRule[]
            {
                new("FortniteClient-Win64-Shipping", ProcessRole.Game),
                new("FortniteLauncher", ProcessRole.Launcher)
            },
            StatusPage = new("https://status.epicgames.com/", true, "Epic Games public status (covers Fortnite)")
        },
        new()
        {
            Id = "apex",
            DisplayName = "Apex Legends",
            Publisher = "Electronic Arts / Respawn",
            ProcessRules = new ProcessRule[]
            {
                new("r5apex_dx12", ProcessRole.Game),
                new("r5apex", ProcessRole.Game)
            },
            StatusPage = new("https://help.ea.com/en/server-status/", true, "EA server status")
        },
        new()
        {
            Id = "cs2",
            DisplayName = "Counter-Strike 2",
            Publisher = "Valve",
            ProcessRules = new ProcessRule[] { new("cs2", ProcessRole.Game) }
        },
        new()
        {
            Id = "wow",
            DisplayName = "World of Warcraft",
            Publisher = "Blizzard Entertainment",
            ProcessRules = new ProcessRule[]
            {
                new("Wow", ProcessRole.Game, new[] { "World of Warcraft" }),
                new("WowClassic", ProcessRole.Game, new[] { "World of Warcraft" }),
                new("Wow-64", ProcessRole.Game, new[] { "World of Warcraft" })
            }
        },
        new()
        {
            Id = "marvelrivals",
            DisplayName = "Marvel Rivals",
            Publisher = "NetEase Games",
            ProcessRules = new ProcessRule[] { new("Marvel-Win64-Shipping", ProcessRole.Game) }
        },
        new()
        {
            Id = "poe",
            DisplayName = "Path of Exile",
            Publisher = "Grinding Gear Games",
            ProcessRules = new ProcessRule[]
            {
                new("PathOfExile", ProcessRole.Game),
                new("PathOfExile_x64", ProcessRole.Game),
                new("PathOfExileSteam", ProcessRole.Game),
                new("PathOfExile_x64Steam", ProcessRole.Game),
                new("PathOfExile_x64EGS", ProcessRole.Game),
                new("PathOfExile2", ProcessRole.Game, null, "Path of Exile 2 shares this profile")
            }
        }
    };

    public static GameProfile? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every executable name referenced by any built-in profile.</summary>
    public static IReadOnlyCollection<string> AllProcessNames { get; } =
        All.SelectMany(p => p.AllProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
