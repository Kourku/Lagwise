using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lagwise.Core.Services;

/// <summary>
/// "Start Lagwise when I sign in to Windows": the per-user Run entry, read together with the switch Windows keeps for it
/// under Task Manager > Startup apps (and Settings > Apps > Startup), so Lagwise never claims it starts when Windows
/// was told not to start it.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsIntegrationService
{
    public const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Task Manager and Settings record that the user turned a startup entry off.</summary>
    public const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string AppName = "Lagwise";

    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly Func<string?> _exePath;
    private IStartupTaskBackend? _startupTask;

    /// <param name="runKey">Override for tests (a scratch key under HKEY_CURRENT_USER).</param>
    /// <param name="approvedKey">Override for tests.</param>
    /// <param name="exePath">Override for tests: the program the entry should start.</param>
    public WindowsIntegrationService(string? runKey = null, string? approvedKey = null, Func<string?>? exePath = null)
    {
        _runKey = runKey ?? RunRegistryKey;
        _approvedKey = approvedKey ?? StartupApprovedKey;
        _exePath = exePath ?? (() => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName);
    }

    /// <summary>
    /// The Microsoft Store edition starts at sign-in through a startup task declared in its manifest, not through the Run key.
    /// Once a backend is set, it answers every question about starting with Windows.
    /// </summary>
    public void UseStartupTask(IStartupTaskBackend backend) => _startupTask = backend;

    /// <summary>True when "start with Windows" is a packaged app's startup task (so only the user can switch it back on after turning it off).</summary>
    public bool UsesStartupTask => _startupTask != null;

    /// <summary>True only when Windows will actually start Lagwise at sign-in.</summary>
    public bool IsStartWithWindowsEnabled() =>
        _startupTask != null ? _startupTask.GetState() == StartupTaskState.Enabled : RegisteredCommand() != null && !IsTurnedOffInWindows();

    /// <summary>The entry exists, but the user turned it off in Task Manager or Settings.</summary>
    public bool IsTurnedOffInWindows()
    {
        if (_startupTask != null) return _startupTask.GetState() is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_approvedKey, false);
            // first byte: even (02, 06) = on, odd (03, 07) = turned off; absent = on
            return RegisteredCommand() != null && key?.GetValue(AppName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool SetStartWithWindows(bool enable)
    {
        if (_startupTask != null)
        {
            if (!enable) { _startupTask.Disable(); return true; }
            return _startupTask.Enable() == StartupTaskState.Enabled;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKey, true);
            if (key == null) return false;

            if (enable)
            {
                string? exePath = _exePath();
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
                key.SetValue(AppName, CommandFor(exePath));
                // asked for in Lagwise: lift an earlier "off" from Task Manager, which would otherwise still win
                using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, true);
                approved?.DeleteValue(AppName, false);
                return true;
            }

            key.DeleteValue(AppName, false);
            // and Windows' on/off switch for it, so Task Manager keeps no entry for a program that is gone
            using (var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, true)) approved?.DeleteValue(AppName, false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// For the uninstaller: removes the startup entry when it starts the Lagwise in <paramref name="installFolder"/>,
    /// so Windows does not try to start a deleted program at every sign-in. Another copy's entry is left alone.
    /// </summary>
    public bool RemoveStartupEntryFor(string installFolder)
    {
        string? command = RegisteredCommand();
        if (command == null || ProgramIn(command) is not { } program) return false;
        string folder = Path.GetFullPath(installFolder).TrimEnd('\\', '/');
        string? programFolder = Path.GetDirectoryName(Path.GetFullPath(program));
        if (!string.Equals(programFolder, folder, StringComparison.OrdinalIgnoreCase)) return false;
        return SetStartWithWindows(false);
    }

    /// <summary>
    /// A portable Lagwise that was moved leaves an entry pointing at nothing, and Windows silently skips it. When the
    /// program it names no longer exists, point the entry at this copy. An entry for another copy that still exists is
    /// left alone. Returns true when the entry was repaired.
    /// </summary>
    public bool RepairStartupEntry()
    {
        if (_startupTask != null) return false; // Windows moves a packaged app's startup task along with the app
        string? command = RegisteredCommand();
        string? exePath = _exePath();
        if (command == null || string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        string? registered = ProgramIn(command);
        if (registered == null || File.Exists(registered)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey, true);
            if (key == null) return false;
            key.SetValue(AppName, CommandFor(exePath));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lagwise was called NetPulse: its startup entry had that name. The old entry is removed, and when it was switched on
    /// (not turned off in Task Manager) Lagwise takes its place. Returns true when an old entry was found.
    /// </summary>
    public bool MigrateStartupEntry(string legacyName)
    {
        if (_startupTask != null) return false; // the Store edition never had a NetPulse Run entry to take over
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(_runKey, true);
            if (run?.GetValue(legacyName) is not string) return false;
            bool wasOn;
            using (var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, true))
            {
                wasOn = !(approved?.GetValue(legacyName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1);
                approved?.DeleteValue(legacyName, false);
            }
            run.DeleteValue(legacyName, false);
            if (wasOn && RegisteredCommand() == null) SetStartWithWindows(true);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static string CommandFor(string exePath) => $"\"{exePath}\" --minimized";

    /// <summary>The program path in a Run command line (quoted or not).</summary>
    public static string? ProgramIn(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(" --", StringComparison.Ordinal);
        return (space > 0 ? command[..space] : command).Trim() is { Length: > 0 } path ? path : null;
    }

    private string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey, false);
            return key?.GetValue(AppName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int intVal) return intVal == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return true; // Default to dark mode
    }
}
