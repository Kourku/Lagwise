using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetPulse.Core.Games;

namespace NetPulse.Core.Services;

/// <summary>What kind of program it is, as far as moving lots of data goes.</summary>
public enum ProgramKind { Other, GameLauncher, CloudSync, WindowsUpdate, Torrent, Browser, Streaming, Game }

/// <summary>A program on this PC with internet connections open right now.</summary>
public sealed class OnlineProgram
{
    public string ProcessName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ProgramKind Kind { get; init; }
    public int ProcessCount { get; init; }
    /// <summary>Established TCP connections to addresses on the internet.</summary>
    public int InternetConnections { get; init; }
    /// <summary>Distinct internet addresses those connections go to.</summary>
    public int RemoteHosts { get; init; }
    /// <summary>Open UDP sockets (Windows does not say where they send).</summary>
    public int UdpSockets { get; init; }
    /// <summary>Launchers, cloud sync, Windows Update, torrent clients, browsers and streaming apps can fill a connection.</summary>
    public bool CanUseALot => Kind is not (ProgramKind.Other or ProgramKind.Game);
    public string KindLabel => OnlinePrograms.Label(Kind);
}

/// <summary>
/// "Which programs are online": groups the socket table by owning program. Windows gives the connections of every
/// program without administrator rights, but not how many bytes each one moves, so this answers "who is talking to
/// the internet", not "who is using the bandwidth". Programs known to download or upload a lot are flagged.
/// </summary>
public static class OnlinePrograms
{
    private static readonly Dictionary<string, (string Name, ProgramKind Kind)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steam"] = ("Steam", ProgramKind.GameLauncher),
        ["steamwebhelper"] = ("Steam", ProgramKind.GameLauncher),
        ["steamservice"] = ("Steam", ProgramKind.GameLauncher),
        ["EpicGamesLauncher"] = ("Epic Games Launcher", ProgramKind.GameLauncher),
        ["EpicWebHelper"] = ("Epic Games Launcher", ProgramKind.GameLauncher),
        ["Battle.net"] = ("Battle.net", ProgramKind.GameLauncher),
        ["Agent"] = ("Battle.net", ProgramKind.GameLauncher),
        ["EADesktop"] = ("EA app", ProgramKind.GameLauncher),
        ["EABackgroundService"] = ("EA app", ProgramKind.GameLauncher),
        ["GalaxyClient"] = ("GOG Galaxy", ProgramKind.GameLauncher),
        ["upc"] = ("Ubisoft Connect", ProgramKind.GameLauncher),
        ["UbisoftConnect"] = ("Ubisoft Connect", ProgramKind.GameLauncher),
        ["RiotClientServices"] = ("Riot Client", ProgramKind.GameLauncher),
        ["XboxPcApp"] = ("Xbox app", ProgramKind.GameLauncher),
        ["GamingServices"] = ("Xbox / Microsoft Store", ProgramKind.GameLauncher),
        ["WinStore.App"] = ("Microsoft Store", ProgramKind.GameLauncher),
        ["OneDrive"] = ("OneDrive", ProgramKind.CloudSync),
        ["Dropbox"] = ("Dropbox", ProgramKind.CloudSync),
        ["GoogleDriveFS"] = ("Google Drive", ProgramKind.CloudSync),
        ["iCloudDrive"] = ("iCloud", ProgramKind.CloudSync),
        ["MEGAsync"] = ("MEGA", ProgramKind.CloudSync),
        ["MoUsoCoreWorker"] = ("Windows Update", ProgramKind.WindowsUpdate),
        ["wuauclt"] = ("Windows Update", ProgramKind.WindowsUpdate),
        ["TiWorker"] = ("Windows Update", ProgramKind.WindowsUpdate),
        ["svchost"] = ("Windows services", ProgramKind.WindowsUpdate),
        ["qbittorrent"] = ("qBittorrent", ProgramKind.Torrent),
        ["uTorrent"] = ("µTorrent", ProgramKind.Torrent),
        ["BitTorrent"] = ("BitTorrent", ProgramKind.Torrent),
        ["transmission-qt"] = ("Transmission", ProgramKind.Torrent),
        ["deluge"] = ("Deluge", ProgramKind.Torrent),
        ["chrome"] = ("Google Chrome", ProgramKind.Browser),
        ["msedge"] = ("Microsoft Edge", ProgramKind.Browser),
        ["firefox"] = ("Firefox", ProgramKind.Browser),
        ["opera"] = ("Opera", ProgramKind.Browser),
        ["brave"] = ("Brave", ProgramKind.Browser),
        ["vivaldi"] = ("Vivaldi", ProgramKind.Browser),
        ["obs64"] = ("OBS Studio", ProgramKind.Streaming),
        ["Spotify"] = ("Spotify", ProgramKind.Streaming),
        ["Netflix"] = ("Netflix", ProgramKind.Streaming),
        ["Discord"] = ("Discord", ProgramKind.Other),
    };

    public static string Label(ProgramKind kind) => kind switch
    {
        ProgramKind.GameLauncher => "Game launcher / store: downloads updates",
        ProgramKind.CloudSync => "Cloud sync: uploads and downloads files",
        ProgramKind.WindowsUpdate => "Windows: Update, Delivery Optimization, services",
        ProgramKind.Torrent => "Torrent client: can fill the connection",
        ProgramKind.Browser => "Web browser: downloads, video",
        ProgramKind.Streaming => "Streaming / broadcasting",
        ProgramKind.Game => "Game",
        _ => ""
    };

    /// <summary>
    /// Names from the game profiles: game processes are games, their launchers and clients are launchers (they patch).
    /// Built-in names win for shared launchers such as the Riot Client.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Name, ProgramKind Kind)> FromProfiles(IEnumerable<GameProfile> profiles)
    {
        var map = new Dictionary<string, (string, ProgramKind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
            foreach (var r in p.ProcessRules)
                map.TryAdd(r.ExecutableName, r.Role == ProcessRole.Game ? (p.DisplayName, ProgramKind.Game) : ($"{p.DisplayName} (client)", ProgramKind.GameLauncher));
        return map;
    }

    public static (string Name, ProgramKind Kind) Classify(string processName, IReadOnlyDictionary<string, (string Name, ProgramKind Kind)>? games = null)
    {
        if (Known.TryGetValue(processName, out var k)) return k;
        if (games != null && games.TryGetValue(processName, out var g)) return g;
        return (processName, ProgramKind.Other);
    }

    /// <summary>Groups connections by program name. NetPulse itself and connections inside this PC or home network are left out.</summary>
    public static IReadOnlyList<OnlineProgram> Summarize(IReadOnlyList<RawConnection> connections, Func<int, string?> processNameOf,
        IReadOnlyDictionary<string, (string Name, ProgramKind Kind)>? games = null, int ownPid = -1)
    {
        var byName = new Dictionary<string, (HashSet<int> Pids, int Tcp, HashSet<string> Hosts, int Udp)>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<int, string?>();

        foreach (var c in connections)
        {
            if (c.Pid <= 4 || c.Pid == ownPid) continue;   // System Idle / System, and NetPulse itself
            if (!names.TryGetValue(c.Pid, out var name)) names[c.Pid] = name = processNameOf(c.Pid);
            if (string.IsNullOrEmpty(name)) continue;

            bool internetTcp = c.Protocol == TransportProtocol.Tcp && c.State == ConnectionState.Established
                               && NetworkScopes.Classify(c.RemoteAddress) == AddressScope.Public;
            bool udp = c.Protocol == TransportProtocol.Udp && NetworkScopes.Classify(c.LocalAddress) is not AddressScope.Loopback;
            if (!internetTcp && !udp) continue;

            if (!byName.TryGetValue(name, out var e)) e = (new HashSet<int>(), 0, new HashSet<string>(), 0);
            e.Pids.Add(c.Pid);
            if (internetTcp) { e.Tcp++; e.Hosts.Add(c.RemoteAddress!.ToString()); }
            else e.Udp++;
            byName[name] = e;
        }

        // several process names can be one program (steam + steamwebhelper): merge by display name
        return byName
            .Select(kv => (kv.Key, kv.Value, Info: Classify(kv.Key, games)))
            .Where(x => x.Value.Tcp > 0)   // UDP alone says nothing about the internet (discovery, local services)
            .GroupBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OnlineProgram
            {
                ProcessName = g.First().Key,
                DisplayName = g.Key,
                Kind = g.First().Info.Kind,
                ProcessCount = g.SelectMany(x => x.Value.Pids).Distinct().Count(),
                InternetConnections = g.Sum(x => x.Value.Tcp),
                RemoteHosts = g.SelectMany(x => x.Value.Hosts).Distinct().Count(),
                UdpSockets = g.Sum(x => x.Value.Udp)
            })
            .OrderByDescending(p => p.CanUseALot)
            .ThenByDescending(p => p.InternetConnections)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Process name for a PID, or null when it has exited or is not visible to a normal user.</summary>
    public static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>"Steam (12 connections), OneDrive (3)": the programs that can use a lot, for a timeline entry.</summary>
    public static string HeavyCandidatesText(IReadOnlyList<OnlineProgram> programs, int max = 4)
    {
        var heavy = programs.Where(p => p.CanUseALot).Take(max).ToList();
        if (heavy.Count == 0) return "";
        return string.Join(", ", heavy.Select((p, i) => i == 0 ? $"{p.DisplayName} ({p.InternetConnections} connection{(p.InternetConnections == 1 ? "" : "s")})" : $"{p.DisplayName} ({p.InternetConnections})"));
    }
}
