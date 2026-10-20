using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NetPulse.Core.Games;

/// <summary>One game from Windows' own game list: the program Windows saw run, and when.</summary>
/// <param name="TitleId">Windows' id for the title; programs of one game share it (a game and its client, say).</param>
public sealed record WindowsGameEntry(string ExePath, DateTime? LastPlayedUtc = null, string? TitleId = null);

public interface IGameListSource
{
    IReadOnlyList<WindowsGameEntry> Read();
}

/// <summary>
/// Windows' list of the games it has seen run on this PC, kept for every user by Windows 10 and 11 (the list Game Bar
/// and Game Mode use) under <c>HKEY_CURRENT_USER\System\GameConfigStore\Children</c>. Entries Windows matched to a real
/// program carry its full path (<c>MatchedExeFullPath</c>) and when it last ran (<c>LastAccessed</c>). Read only;
/// an empty or missing list (Game Bar turned off by policy, say) simply yields no games.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameListSource : IGameListSource
{
    public const string KeyPath = @"System\GameConfigStore\Children";

    public IReadOnlyList<WindowsGameEntry> Read()
    {
        var list = new List<WindowsGameEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (root == null) return list;
            foreach (string name in root.GetSubKeyNames().Take(5000))
            {
                using var key = root.OpenSubKey(name, writable: false);
                if (key?.GetValue("MatchedExeFullPath") is not string path || !GameLibrary.IsUsableExePath(path)) continue;
                DateTime? last = key.GetValue("LastAccessed") is long ft ? FromFileTime(ft) : null;
                string? title = key.GetValue("TitleId") is int id && id != 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                list.Add(new WindowsGameEntry(path, last, title));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // an unreadable list is an empty list
        }
        return list;
    }

    private static DateTime? FromFileTime(long ft)
    {
        try { return ft > 0 ? DateTime.FromFileTimeUtc(ft) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>
/// The games NetPulse watches for: its built-in games, the games Windows lists on this PC, and programs the user added.
/// Everything is found on the PC NetPulse runs on; nothing depends on a particular PC's folders.
/// </summary>
public static class GameLibrary
{
    public const string WindowsPrefix = "win:";
    public const string UserPrefix = "user:";

    /// <param name="exists">Whether a file exists (injected for tests); a game whose program is gone is left out.</param>
    /// <param name="versionInfo">The product name and description a program carries (injected for tests).</param>
    public static IReadOnlyList<GameProfile> Build(
        IReadOnlyList<GameProfile> builtIns,
        IEnumerable<WindowsGameEntry> windowsGames,
        IEnumerable<string> addedByUser,
        IEnumerable<string>? ignoredIds = null,
        Func<string, bool>? exists = null,
        Func<string, (string? Product, string? Description)>? versionInfo = null)
    {
        exists ??= File.Exists;
        versionInfo ??= ReadVersionInfo;
        var ignored = new HashSet<string>(ignoredIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var companionNames = new HashSet<string>(builtIns.SelectMany(p => p.ProcessRules.Where(r => r.Role != ProcessRole.Game).Select(r => r.ExecutableName)), StringComparer.OrdinalIgnoreCase);

        var result = builtIns.ToList();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // exe paths already in the library

        // Windows' list: a built-in game's own program enriches that game; a built-in game's client or launcher is
        // left out (it is not the game); any other program becomes a game of its own
        var entries = windowsGames
            .Where(e => IsUsableExePath(e.ExePath))
            .GroupBy(e => Normalize(e.ExePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.LastPlayedUtc ?? DateTime.MinValue).First() with { ExePath = g.Key })
            .Where(e => exists(e.ExePath))
            .ToList();

        var others = new List<WindowsGameEntry>();
        foreach (var e in entries)
        {
            int i = result.FindIndex(p => p.RulesFor(ProcessRole.Game).Any(r => Fits(r, e.ExePath)));
            if (i >= 0)
            {
                var p = result[i];
                result[i] = p with { ExecutablePath = p.ExecutablePath ?? e.ExePath, LastPlayedUtc = Later(p.LastPlayedUtc, e.LastPlayedUtc) };
                known.Add(e.ExePath);
            }
            else if (companionNames.Contains(Stem(e.ExePath)))
            {
                known.Add(e.ExePath);
            }
            else
            {
                others.Add(e);
            }
        }

        foreach (var group in others.GroupBy(e => e.TitleId ?? "path:" + e.ExePath, StringComparer.OrdinalIgnoreCase))
        {
            var newestFirst = group.OrderByDescending(e => e.LastPlayedUtc ?? DateTime.MinValue).ToList();
            var main = newestFirst[0];
            var (product, description) = versionInfo(main.ExePath);
            result.Add(new GameProfile
            {
                Id = WindowsPrefix + (main.TitleId ?? StableId(main.ExePath)),
                DisplayName = NameFor(main.ExePath, product, description),
                Source = GameSource.Windows,
                ExecutablePath = main.ExePath,
                LastPlayedUtc = newestFirst.Max(e => e.LastPlayedUtc),
                ProcessRules = newestFirst.Select(e => new ProcessRule(Stem(e.ExePath), ProcessRole.Game, ExactPath: e.ExePath)).ToList()
            });
            foreach (var e in newestFirst) known.Add(e.ExePath);
        }

        foreach (string raw in addedByUser)
        {
            if (!IsUsableExePath(raw)) continue;
            string path = Normalize(raw);
            if (!known.Add(path)) continue;     // already found another way
            var (product, description) = exists(path) ? versionInfo(path) : (null, null);
            result.Add(new GameProfile
            {
                Id = UserPrefix + StableId(path),
                DisplayName = NameFor(path, product, description),
                Source = GameSource.AddedByYou,
                ExecutablePath = path,
                ProcessRules = new[] { new ProcessRule(Stem(path), ProcessRole.Game, ExactPath: path) }
            });
        }

        return result.Where(p => !ignored.Contains(p.Id)).ToList();
    }

    /// <summary>True for a full path to an .exe file NetPulse can compare process locations with.</summary>
    public static bool IsUsableExePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try
        {
            return Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).Length > 4;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A stable id for a program on this PC (the same every time NetPulse starts).</summary>
    public static string StableId(string exePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(exePath).ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    private static bool Fits(ProcessRule rule, string path) =>
        string.Equals(rule.ExecutableName, Stem(path), StringComparison.OrdinalIgnoreCase)
        && (rule.PathHints is not { Length: > 0 } || rule.PathHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase)));

    private static DateTime? Later(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : (a > b ? a : b);

    private static (string?, string?) ReadVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.ProductName, info.FileDescription);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    // ------------------------------------------------------------------ names

    /// <summary>Product names that say nothing about the game (engine and launcher defaults).</summary>
    private static readonly HashSet<string> MeaninglessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BootstrapPackagedGame", "UnrealGame", "Unreal Engine", "UE4", "UE5", "UE4Game", "Game", "Client", "Launcher",
        "Application", "Unity", "Unity Player", "Win64", "Shipping", "Epic Games", "Godot Engine", "Main", "App"
    };

    /// <summary>Folders that hold a game's program but are not its name (engine and store layouts).</summary>
    private static readonly HashSet<string> BuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "binary", "win32", "win64", "wingdk", "x64", "x86", "x86_64", "amd64", "retail",
        "release", "shipping", "game", "client", "client_pc", "root", "pc", "live", "_retail_", "_classic_", "_classic_era_",
        "_ptr_", "current", "app", "build", "builds", "dist", "windows", "windowsnoeditor", "content"
    };

    /// <summary>Folders that hold many games: reaching one means the program's own name is the best name left.</summary>
    private static readonly HashSet<string> LibraryFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "steamapps", "steamlibrary", "steam", "games", "game", "program files", "program files (x86)", "epic games",
        "gog games", "gog galaxy", "xboxgames", "riot games", "ubisoft", "ubisoft game launcher", "ea games", "origin games",
        "electronic arts", "battle.net", "windowsapps", "downloads", "desktop"
    };

    /// <summary>
    /// A readable name for a game program: its product name when that means something, else the folder named after the
    /// game (climbing past engine build folders such as Binaries\Win64), else the program's own name.
    /// </summary>
    public static string NameFor(string exePath, string? productName = null, string? fileDescription = null)
    {
        string stem = Path.GetFileNameWithoutExtension(exePath);
        string project = StripBuildSuffix(stem);
        foreach (var candidate in new[] { productName, fileDescription })
        {
            string? clean = CleanName(candidate);
            if (clean != null && !MeaninglessNames.Contains(clean) && !clean.Equals(stem, StringComparison.OrdinalIgnoreCase) && !clean.Equals(project, StringComparison.OrdinalIgnoreCase))
                return clean;
        }

        string? dir = SafeDirectory(exePath);
        bool passedBinaries = false, skippedProject = false;
        for (int depth = 0; dir != null && depth < 8; depth++, dir = SafeDirectory(dir))
        {
            string folder = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folder) || LibraryFolders.Contains(folder)) break;
            if (BuildFolders.Contains(folder))
            {
                if (folder.Equals("binaries", StringComparison.OrdinalIgnoreCase)) passedBinaries = true;
                continue;
            }
            // Unreal Engine layout: <Game>\<Project>\Binaries\Win64\<Project>.exe; the project folder is often a code name
            if (passedBinaries && !skippedProject)
            {
                skippedProject = true;
                string? parent = SafeDirectory(dir);
                string parentName = parent == null ? "" : Path.GetFileName(parent);
                if (parentName.Length > 0 && !LibraryFolders.Contains(parentName) && !BuildFolders.Contains(parentName)) continue;
            }
            return Pretty(folder);
        }
        return Pretty(project);
    }

    private static string? SafeDirectory(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }
    }

    private static string StripBuildSuffix(string stem)
    {
        foreach (string suffix in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "-Win32-Shipping", "-Shipping", "_x64", "_dx12", "_dx11", " x64", "64" })
            if (stem.Length > suffix.Length + 1 && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return stem[..^suffix.Length];
        return stem;
    }

    private static string? CleanName(string? name)
    {
        string? clean = UntrustedText.Clean(name, 80);
        if (string.IsNullOrWhiteSpace(clean)) return null;
        foreach (string mark in new[] { "(TM)", "(R)", "™", "®", "©" }) clean = clean.Replace(mark, "", StringComparison.OrdinalIgnoreCase);
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length >= 2 && clean.Length <= 60 && clean.Any(char.IsLetter) ? clean : null;
    }

    private static string Pretty(string name)
    {
        string s = string.Join(' ', name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return UntrustedText.Clean(s.Length == 0 ? name : s, 60) ?? name;
    }
}
