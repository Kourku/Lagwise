using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPulse.Core.Games;

/// <param name="Name">Process name without ".exe".</param>
public sealed record ProcessInfo(int Pid, string Name, DateTime? StartTimeUtc = null, string? ExecutablePath = null);

/// <summary>Abstraction over "what is running" so detection can be tested with simulated processes.</summary>
public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> GetProcesses();
}

/// <summary>
/// Reads the process list through the normal .NET/Win32 APIs; needs no administrator rights. Expensive details
/// (start time, image path) are only read for processes whose name appears in <c>interestingNames</c>.
/// </summary>
public sealed class SystemProcessSource : IProcessSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly HashSet<string> _interesting;

    public SystemProcessSource(IEnumerable<string> interestingNames)
    {
        _interesting = new HashSet<string>(interestingNames, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { continue; }

                if (!_interesting.Contains(name))
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

    private static string? TryGetImagePath(int pid)
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
    /// <summary>Only launcher/helper/anti-cheat/crash-reporter processes exist; the game itself is not running.</summary>
    LauncherOnly,
    Running,
    /// <summary>A process with the game's name exists but its install path does not match, so it is not claimed as the game.</summary>
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
    /// <summary>Launcher/helper/anti-cheat/crash-reporter processes (never counted as the game).</summary>
    public IReadOnlyList<DetectedProcess> CompanionProcesses { get; init; } = Array.Empty<DetectedProcess>();
    /// <summary>Name-matching processes whose path contradicted the profile.</summary>
    public IReadOnlyList<DetectedProcess> UnconfirmedProcesses { get; init; } = Array.Empty<DetectedProcess>();
    public DetectionConfidence Confidence { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Oldest game process, normally the parent in a multi-process game.</summary>
    public DetectedProcess? Primary => GameProcesses.OrderBy(p => p.StartTimeUtc ?? DateTime.MaxValue).ThenBy(p => p.Pid).FirstOrDefault();
    public IReadOnlySet<int> GamePids => GameProcesses.Select(p => p.Pid).ToHashSet();
}

public static class GameProcessDetector
{
    public static GameProcessState Detect(GameProfile profile, IEnumerable<ProcessInfo> processes)
    {
        var rules = profile.ProcessRules
            .GroupBy(r => r.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var games = new List<DetectedProcess>();
        var companions = new List<DetectedProcess>();
        var unconfirmed = new List<DetectedProcess>();

        foreach (var p in processes)
        {
            string name = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name;
            if (!rules.TryGetValue(name, out var rule)) continue;

            bool hasHints = rule.PathHints is { Length: > 0 };
            bool pathKnown = !string.IsNullOrEmpty(p.ExecutablePath);
            bool pathMatches = hasHints && pathKnown && rule.PathHints!.Any(h => p.ExecutablePath!.Contains(h, StringComparison.OrdinalIgnoreCase));
            var detected = new DetectedProcess(p.Pid, name, rule.Role, p.StartTimeUtc, p.ExecutablePath, PathConfirmed: pathMatches);

            if (rule.Role != ProcessRole.Game)
            {
                companions.Add(detected);
            }
            else if (hasHints && pathKnown && !pathMatches)
            {
                unconfirmed.Add(detected);
            }
            else
            {
                games.Add(detected);
            }
        }

        if (games.Count > 0)
        {
            // Confidence is lower when the profile expects a path hint but the path could not be read (e.g. protected process).
            bool allConfirmed = games.All(g => g.PathConfirmed || !(rules[g.Name].PathHints is { Length: > 0 }));
            var confidence = allConfirmed ? profile.DetectionConfidence : Lower(profile.DetectionConfidence);
            return new GameProcessState
            {
                Profile = profile,
                State = GameRunState.Running,
                GameProcesses = games,
                CompanionProcesses = companions,
                UnconfirmedProcesses = unconfirmed,
                Confidence = confidence,
                Message = (games.Count == 1
                    ? $"{profile.DisplayName} is running (process {games[0].Name}, PID {games[0].Pid})."
                    : $"{profile.DisplayName} is running ({games.Count} game processes).")
                    + (allConfirmed ? "" : " The install folder could not be verified, so confidence is reduced.")
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
                Confidence = DetectionConfidence.Low,
                Message = $"A process named {unconfirmed[0].Name} is running, but it is not in the expected {profile.DisplayName} install folder, so it was not treated as the game."
            };
        }

        if (companions.Count > 0)
        {
            string roles = string.Join(", ", companions.Select(c => c.Role switch
            {
                ProcessRole.CrashReporter => "crash reporter",
                ProcessRole.AntiCheat => "anti-cheat",
                _ => c.Role.ToString().ToLowerInvariant()
            }).Distinct());
            return new GameProcessState
            {
                Profile = profile,
                State = GameRunState.LauncherOnly,
                CompanionProcesses = companions,
                Confidence = profile.DetectionConfidence,
                Message = $"Only {profile.DisplayName} support programs ({roles}) are running; the game itself is not."
            };
        }

        return new GameProcessState
        {
            Profile = profile,
            State = GameRunState.NotRunning,
            Confidence = profile.DetectionConfidence,
            Message = $"{profile.DisplayName} is not running."
        };
    }

    private static DetectionConfidence Lower(DetectionConfidence c) => c switch
    {
        DetectionConfidence.High => DetectionConfidence.Medium,
        _ => DetectionConfidence.Low
    };
}
