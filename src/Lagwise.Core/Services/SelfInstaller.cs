using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lagwise.Core.Services;

/// <summary>
/// "Install Lagwise for this user", built into the app: the portable Lagwise.exe copies itself to
/// <c>%LOCALAPPDATA%\Programs\Lagwise</c>, adds a Start menu entry (and, if asked, a desktop shortcut) and lists itself
/// in Settings > Apps, whose Uninstall runs <c>Lagwise.exe --uninstall</c>. No administrator rights, no second download:
/// everything is per-user (the profile and HKEY_CURRENT_USER). User data stays in %LOCALAPPDATA%\Lagwise either way.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelfInstaller
{
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise";
    public const string ExeName = "Lagwise.exe";
    public const string ShortcutName = "Lagwise.lnk";
    /// <summary>Left in an install folder by the separate setup program of 1.0.0; removed when this replaces it.</summary>
    private static readonly string[] OldSetupFiles = { "Lagwise-Setup.exe", "app.ico" };

    private readonly string _uninstallKey;
    private readonly string _startMenuDir;
    private readonly string _desktopDir;
    private readonly WindowsIntegrationService? _startup;

    /// <param name="uninstallKey">Override for tests (a scratch key under HKEY_CURRENT_USER).</param>
    /// <param name="startMenuDir">Override for tests.</param>
    /// <param name="desktopDir">Override for tests.</param>
    /// <param name="startup">The "start with Windows" entry, moved to the installed copy when it pointed at this one.</param>
    public SelfInstaller(string? uninstallKey = null, string? startMenuDir = null, string? desktopDir = null, WindowsIntegrationService? startup = null)
    {
        _uninstallKey = uninstallKey ?? UninstallKeyPath;
        _startMenuDir = startMenuDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        _desktopDir = desktopDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _startup = startup;
    }

    public static string DefaultInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lagwise");

    /// <summary>The folder of the installed Lagwise, when one is registered and its program is still there.</summary>
    public string? InstalledDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_uninstallKey, false);
            if (key?.GetValue("InstallLocation") is not string dir || string.IsNullOrWhiteSpace(dir)) return null;
            dir = dir.TrimEnd('\\', '/');
            return File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The version of the installed Lagwise (from its program file), or null.</summary>
    public Version? InstalledVersion() =>
        InstalledDirectory() is { } dir ? VersionOf(Path.Combine(dir, ExeName)) : null;

    /// <summary>True when <paramref name="exePath"/> is the installed copy.</summary>
    public bool IsInstalledCopy(string? exePath) =>
        exePath != null && InstalledDirectory() is { } dir &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(exePath)), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);

    public static Version? VersionOf(string exe)
    {
        try
        {
            string? text = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return null; }
    }

    public sealed record Result(bool Ok, string Message, string? InstalledExe = null);

    /// <summary>
    /// Copies <paramref name="sourceExe"/> (normally the running program) into <paramref name="installDir"/> as Lagwise.exe,
    /// adds the shortcuts and the Settings > Apps entry. Installing over an existing installation updates it.
    /// </summary>
    /// <param name="startMenuShortcut">False only for the build's verification, which must not touch the real Start menu.</param>
    public Result Install(string sourceExe, string installDir, bool desktopShortcut, bool startMenuShortcut = true)
    {
        if (!File.Exists(sourceExe)) return new Result(false, "The Lagwise program file could not be found.");
        installDir = Path.GetFullPath(installDir).TrimEnd('\\', '/');
        string target = Path.Combine(installDir, ExeName);
        if (string.Equals(Path.GetFullPath(sourceExe), target, StringComparison.OrdinalIgnoreCase))
            return new Result(false, "This copy of Lagwise is already the installed one.");

        try
        {
            Directory.CreateDirectory(installDir);
            // copied to a temporary name first, so a failure never leaves half a program where the installed one was
            string partial = target + ".partial";
            File.Copy(sourceExe, partial, true);
            File.Move(partial, target, true);
            // the separate setup program of Lagwise 1.0.0 is no longer needed: its own files go
            foreach (var name in OldSetupFiles)
            {
                string old = Path.Combine(installDir, name);
                if (File.Exists(old)) File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, ex is IOException && File.Exists(target)
                ? "The installed Lagwise is in use. Close it (tray icon > Exit) and try again."
                : "Lagwise could not be copied to " + installDir + ": " + ex.Message);
        }

        RemoveOldNetPulse();
        if (startMenuShortcut) CreateShortcut(Path.Combine(_startMenuDir, ShortcutName), target, installDir);
        string desktopLink = Path.Combine(_desktopDir, ShortcutName);
        if (desktopShortcut) CreateShortcut(desktopLink, target, installDir);
        Register(installDir, target);

        // "start with Windows" was on for the copy being installed: it now starts the installed one
        if (_startup?.StartupProgram() is { } program && string.Equals(Path.GetFullPath(program), Path.GetFullPath(sourceExe), StringComparison.OrdinalIgnoreCase))
            _startup.SetStartWithWindows(true, target);

        return new Result(true, "Lagwise is installed in " + installDir + ".", target);
    }

    /// <summary>
    /// Removes the installation in <paramref name="installDir"/>: shortcuts, the Settings > Apps entry, the startup entry
    /// when it starts this copy, and the program file (after this process has exited, since it may be the one running).
    /// Settings and recordings in %LOCALAPPDATA%\Lagwise are kept.
    /// </summary>
    public Result Uninstall(string installDir, bool deleteFilesAfterExit = true)
    {
        installDir = Path.GetFullPath(installDir).TrimEnd('\\', '/');
        foreach (var dir in new[] { _startMenuDir, _desktopDir })
        {
            string link = Path.Combine(dir, ShortcutName);
            try { if (ShortcutPointsInto(link, installDir)) File.Delete(link); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(_uninstallKey, false); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        _startup?.RemoveStartupEntryFor(installDir);

        if (deleteFilesAfterExit) ScheduleDeletion(installDir);
        return new Result(true, "Lagwise has been removed. Your settings and recordings are kept in " +
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lagwise") + ".");
    }

    /// <summary>
    /// Deletes Lagwise.exe and the folder (only if that leaves it empty) a few seconds from now, by a cmd.exe started
    /// from the Windows folder: the program file cannot be deleted while it runs. A folder name with characters that
    /// could change the command line is refused.
    /// </summary>
    public static bool ScheduleDeletion(string installDir)
    {
        if (!IsSafeForCommandLine(installDir) || !File.Exists(Path.Combine(installDir, ExeName))) return false;
        string script = $"/c timeout /t 3 /nobreak > NUL & del /f /q \"{installDir}\\{ExeName}\" & rmdir \"{installDir}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),   // never by name: a cmd.exe beside Lagwise would run instead
            Arguments = script,
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return true;
    }

    /// <summary>
    /// Lagwise used to be called NetPulse: an installed NetPulse (its shortcuts, its Settings > Apps entry and the three
    /// files its setup put there) is removed so only Lagwise is left. Its data is never touched (Lagwise copied it on its
    /// first start), and a NetPulse that is running is left in place.
    /// </summary>
    private void RemoveOldNetPulse()
    {
        string oldKey = _uninstallKey[..(_uninstallKey.LastIndexOf('\\') + 1)] + "NetPulse";
        string? dir = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(oldKey);
            dir = key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        if (string.IsNullOrWhiteSpace(dir)) return;
        dir = dir.TrimEnd('\\', '/');
        string oldExe = Path.Combine(dir, "NetPulse.exe");
        bool running = Process.GetProcessesByName("NetPulse").Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, oldExe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        });
        if (running) return;
        try
        {
            foreach (var folder in new[] { _desktopDir, _startMenuDir })
            {
                string link = Path.Combine(folder, "NetPulse.lnk");
                if (File.Exists(link)) File.Delete(link);
            }
            foreach (var name in new[] { "NetPulse.exe", "app.ico", "NetPulse-Setup.exe" })
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) File.Delete(file);
            }
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            Registry.CurrentUser.DeleteSubKeyTree(oldKey, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public static bool IsSafeForCommandLine(string path) =>
        Path.IsPathFullyQualified(path) && path.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n' }) < 0;

    private void Register(string installDir, string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_uninstallKey, true);
            if (key == null) return;
            key.SetValue("DisplayName", "Lagwise");
            key.SetValue("DisplayVersion", VersionOf(exe)?.ToString(3) ?? "");
            key.SetValue("Publisher", "Fraxis");
            key.SetValue("DisplayIcon", $"\"{exe}\",0");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("URLInfoAbout", "https://kourku.github.io/Lagwise/");
            key.SetValue("HelpLink", "https://github.com/Kourku/Lagwise/issues");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            // Settings > Apps offers only Uninstall: there is nothing to modify or repair
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            // older setup values that no longer apply
            key.DeleteValue("ModifyPath", false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void CreateShortcut(string link, string target, string workingDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = "Lagwise: was it my connection?";
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // no shortcut: Lagwise is still installed and listed in Settings > Apps
        }
    }

    /// <summary>True when the shortcut starts a program inside <paramref name="installDir"/> (so another program's shortcut of the same name is left alone).</summary>
    public static bool ShortcutPointsInto(string link, string installDir)
    {
        if (!File.Exists(link)) return false;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            string target = (string)shell.CreateShortcut(link).TargetPath;
            return !string.IsNullOrEmpty(target) &&
                   string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            return false;
        }
    }
}
