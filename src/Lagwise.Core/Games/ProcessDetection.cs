using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Lagwise.Core.Games;

/// <param name="Name">Process name without ".exe".</param>
public sealed record ProcessInfo(int Pid, string Name, DateTime? StartTimeUtc = null, string? ExecutablePath = null);

/// <summary>Abstraction over "what is running" so detection can be tested with simulated processes.</summary>
public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> GetProcesses();
}

/// <summary>
/// Reads the process list through the normal .NET/Win32 APIs; needs no administrator rights. Expensive details
/// (start time, image path) are only read for processes whose name belongs to a known game.
/// </summary>
public sealed class SystemProcessSource : IProcessSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private volatile HashSet<string> _interesting;

    public SystemProcessSource(IEnumerable<string> interestingNames)
    {
        _interesting = new HashSet<string>(interestingNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the names worth a closer look (the game list changed). Safe to call while another thread reads.</summary>
    public void SetInterestingNames(IEnumerable<string> names) =>
        _interesting = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var interesting = _interesting;
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { continue; }

                if (!interesting.Contains(name))
                {
                    result.Add(new ProcessInfo(p.Id, name));
                    continue;
                }

                DateTime? start = null;
                try { start = p.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }

                result.Add(new ProcessInfo(p.Id, name, start, TryGetImagePath(p.Id)));
            }
        }
        return result;
    }

    public static string? TryGetImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null; // access denied: path stays unknown rather than guessed
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
}

public enum GameRunState
{
    NotRunning,
    /// <summary>Only the game's own client or launcher is running; the game itself is not.</summary>
    LauncherOnly,
    Running,
    /// <summary>A process with the game's name exists but its location does not match, so it is not claimed as the game.</summary>
    Uncertain
}

/// <summary>Identity of one process instance. Includes the start time so a recycled PID is a different process.</summary>
public readonly record struct ProcessIdentity(int Pid, DateTime? StartTimeUtc);

public sealed record DetectedProcess(int Pid, string Name, ProcessRole Role, DateTime? StartTimeUtc, string? ExecutablePath, bool PathConfirmed)
{
    public ProcessIdentity Identity => new(Pid, StartTimeUtc);
}

public sealed class GameProcessState
{
    public required GameProfile Profile { get; init; }
    public GameRunState State { get; init; }
    /// <summary>Processes with role Game that are accepted as the game.</summary>
    public IReadOnlyList<DetectedProcess> GameProcesses { get; init; } = Array.Empty<DetectedProcess>();
    /// <summary>Client/launcher/helper/crash-reporter processes (never counted as the game).</summary>
    public IReadOnlyList<DetectedProcess> CompanionProcesses { get; init; } = Array.Empty<DetectedProcess>();
    /// <summary>Name-matching processes whose location contradicted the profile.</summary>
    public IReadOnlyList<DetectedProcess> UnconfirmedProcesses { get; init; } = Array.Empty<DetectedProcess>();
    public string Message { get; init; } = string.Empty;

    /// <summary>Oldest game process, normally the parent in a multi-process game.</summary>
    public DetectedProcess? Primary => GameProcesses.OrderBy(p => p.StartTimeUtc ?? DateTime.MaxValue).ThenBy(p => p.Pid).FirstOrDefault();
    public IReadOnlySet<int> GamePids => GameProcesses.Select(p => p.Pid).ToHashSet();
}

public static class GameProcessDetector
{
    public static GameProcessState Detect(GameProfile profile, IEnumerable<ProcessInfo> processes)
    {
        var rules = profile.ProcessRules.ToLookup(r => r.ExecutableName, StringComparer.OrdinalIgnoreCase);

        var games = new List<DetectedProcess>();
        var companions = new List<DetectedProcess>();
        var unconfirmed = new List<DetectedProcess>();

        foreach (var p in processes)
        {
            string name = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name;
            if (!rules.Contains(name)) continue;

            var (rule, verdict) = Match(rules[name], p.ExecutablePath);
            if (rule == null) continue;     // a program of the same name in another place: another program, not a look-alike
            var detected = new DetectedProcess(p.Pid, name, rule.Role, p.StartTimeUtc, p.ExecutablePath, verdict == PathVerdict.Confirmed);

            if (verdict == PathVerdict.Contradicted) unconfirmed.Add(detected);
            else if (rule.Role == ProcessRole.Game) games.Add(detected);
            else companions.Add(detected);
        }

        if (games.Count > 0)
        {
            return new GameProcessState
            {
                Profile = profile,
                State = GameRunState.Running,
                GameProcesses = games,
                CompanionProcesses = companions,
                UnconfirmedProcesses = unconfirmed,
                Message = $"{profile.DisplayName} is running."
            };
        }

        if (unconfirmed.Count > 0)
        {
            return new GameProcessState
            {
                Profile = profile,
                State = GameRunState.Uncertain,
                CompanionProcesses = companions,
                UnconfirmedProcesses = unconfirmed,
                Message = unconfirmed[0].ExecutablePath == null
                    ? $"A program named {unconfirmed[0].Name} is running, but Windows does not say where it is, so it is not taken for {profile.DisplayName}."
                    : $"A program named {unconfirmed[0].Name} is running from another folder, so it is not taken for {profile.DisplayName}."
            };
        }

        if (companions.Any(c => c.Role == ProcessRole.Launcher))
        {
            return new GameProcessState
            {
                Profile = profile,
                State = GameRunState.LauncherOnly,
                CompanionProcesses = companions,
                Message = $"The {profile.DisplayName} client is open, but no match or game is running yet."
            };
        }

        return new GameProcessState
        {
            Profile = profile,
            State = GameRunState.NotRunning,
            CompanionProcesses = companions,
            Message = $"{profile.DisplayName} is not running."
        };
    }

    private enum PathVerdict { Confirmed, Accepted, Contradicted }

    /// <summary>The rule a process of this name satisfies, judged by where it runs from.</summary>
    private static (ProcessRule? Rule, PathVerdict Verdict) Match(IEnumerable<ProcessRule> candidates, string? path)
    {
        ProcessRule? contradicted = null;
        foreach (var rule in candidates)
        {
            if (rule.ExactPath != null)
            {
                if (path == null) { contradicted ??= rule; continue; }               // cannot tell: not claimed
                if (SamePath(path, rule.ExactPath)) return (rule, PathVerdict.Confirmed);
                continue;                                                            // same file name, another program
            }
            if (rule.PathHints is not { Length: > 0 }) return (rule, PathVerdict.Accepted);
            if (path == null) return (rule, PathVerdict.Accepted);                   // protected process: the name has to do
            if (rule.PathHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase))) return (rule, PathVerdict.Confirmed);
            contradicted ??= rule;
        }
        return contradicted == null ? (null, PathVerdict.Contradicted) : (contradicted, PathVerdict.Contradicted);
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
