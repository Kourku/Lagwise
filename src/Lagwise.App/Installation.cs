using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Lagwise.Core;
using Lagwise.Core.Services;

namespace Lagwise.App;

/// <summary>
/// The portable Lagwise.exe installs itself for this user (Settings > Installation, or the one-time offer on the
/// Overview), and Settings > Apps uninstalls it with <c>Lagwise.exe --uninstall</c>. The work is in
/// <see cref="SelfInstaller"/>; this is the part that talks to the user and hands over to the installed copy.
/// </summary>
internal static class Installation
{
    public enum Kind
    {
        /// <summary>The Store edition (Windows installs it) or a test run with its own data folder: nothing to offer.</summary>
        NotOffered,
        /// <summary>A portable copy and no installed Lagwise.</summary>
        Portable,
        /// <summary>A portable copy newer than the installed Lagwise: it can update it.</summary>
        PortableNewer,
        /// <summary>A portable copy, and an installed Lagwise of the same or a newer version.</summary>
        PortableBesideInstalled,
        /// <summary>This is the installed copy.</summary>
        Installed
    }

    public static bool Offered =>
        !PackageContext.IsPackaged && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SettingsService.DataDirectoryVariable));

    private static SelfInstaller NewInstaller() => new(startup: App.Host?.WindowsIntegration ?? new WindowsIntegrationService());

    public static (Kind Kind, string? InstalledDir, Version? InstalledVersion) Describe()
    {
        if (!Offered || Environment.ProcessPath is not { } exe) return (Kind.NotOffered, null, null);
        var installer = NewInstaller();
        string? dir = installer.InstalledDirectory();
        if (dir == null) return (Kind.Portable, null, null);
        if (installer.IsInstalledCopy(exe)) return (Kind.Installed, dir, installer.InstalledVersion());
        Version? installed = installer.InstalledVersion(), mine = SelfInstaller.VersionOf(exe);
        return (installed != null && mine != null && mine > installed ? Kind.PortableNewer : Kind.PortableBesideInstalled, dir, installed);
    }

    /// <summary>
    /// Installs (or updates) from the window: copies this program, then starts the installed copy and closes this one,
    /// which saves a recording in progress first. Returns a message for the page when it did not go ahead.
    /// </summary>
    public static string? InstallFromWindow(bool desktopShortcut)
    {
        if (Environment.ProcessPath is not { } exe) return "Lagwise could not find its own program file.";
        var result = NewInstaller().Install(exe, SelfInstaller.DefaultInstallDirectory(), desktopShortcut);
        if (!result.Ok || result.InstalledExe == null) return result.Message;

        MessageBox.Show(
            "Lagwise is installed for you in " + Path.GetDirectoryName(result.InstalledExe) + ".\n\n" +
            "It is in the Start menu and in Settings > Apps, where you can uninstall it. Your settings and recordings are unchanged.\n\n" +
            "The installed Lagwise opens now and this copy closes. You can delete the downloaded copy afterwards.",
            "Lagwise installed", MessageBoxButton.OK, MessageBoxImage.Information);

        // the installed copy waits for this one to exit before it starts, so it does not just hand its start back to us
        try
        {
            Process.Start(new ProcessStartInfo(result.InstalledExe)
            {
                ArgumentList = { "--after-install", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                WorkingDirectory = Path.GetDirectoryName(result.InstalledExe)!,
                UseShellExecute = false
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Installed, but the installed copy could not be started: open Lagwise from the Start menu.";
        }
        if (Application.Current.MainWindow is MainWindow main) main.ExitApplication();
        Application.Current.Shutdown();
        return null;
    }

    /// <summary>
    /// <c>--after-install &lt;pid&gt;</c>: the copy that installed this one is closing. Wait for it (at most 30 s), so this
    /// start becomes the running Lagwise instead of handing itself over to the copy that is going away.
    /// </summary>
    public static void WaitForInstallingCopy(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--after-install", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(30000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// <c>Lagwise.exe --install [--desktop-shortcut] [--quiet]</c>: installs this copy without opening a window. The
    /// build's verification adds <c>--install-dir &lt;folder&gt; --no-shortcuts</c> so it never touches the real Start
    /// menu or installation folder. Exit code 0 when installed.
    /// </summary>
    public static int RunInstall(string[] args)
    {
        bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        if (!Offered || Environment.ProcessPath is not { } exe)
        {
            if (!quiet) MessageBox.Show("This edition of Lagwise is installed by Windows.", "Lagwise", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }
        string dir = SelfInstaller.DefaultInstallDirectory();
        int d = Array.FindIndex(args, a => a.Equals("--install-dir", StringComparison.OrdinalIgnoreCase));
        if (d >= 0)
        {
            if (d + 1 >= args.Length || !Path.IsPathFullyQualified(args[d + 1])) return 1;
            dir = args[d + 1];
        }
        bool shortcuts = !args.Contains("--no-shortcuts", StringComparer.OrdinalIgnoreCase);
        var result = NewInstaller().Install(exe, dir, shortcuts && args.Contains("--desktop-shortcut", StringComparer.OrdinalIgnoreCase), startMenuShortcut: shortcuts);
        if (!quiet) MessageBox.Show(result.Message, "Lagwise", MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// <c>Lagwise.exe --uninstall [--quiet]</c>, which Settings > Apps runs: asks first (unless quiet), closes a running
    /// Lagwise (it saves what it is recording), removes the installation and keeps the user's data. Exit code 0 when removed.
    /// </summary>
    public static int RunUninstall(string[] args)
    {
        bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        if (PackageContext.IsPackaged)
        {
            if (!quiet) MessageBox.Show("This edition of Lagwise is uninstalled from Settings > Apps.", "Lagwise", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }
        var installer = NewInstaller();
        string? dir = installer.InstalledDirectory();
        if (dir == null)
        {
            if (!quiet) MessageBox.Show("Lagwise is not installed for this user. A downloaded copy is removed by deleting its file.", "Lagwise", MessageBoxButton.OK, MessageBoxImage.Information);
            return 3;
        }
        if (!quiet && MessageBox.Show(
                "Uninstall Lagwise?\n\nThe program, its Start menu entry and its entry in Settings > Apps are removed. " +
                "Your settings and recordings stay in " + SettingsService.DefaultDirectory() + " (delete that folder too if you want them gone).",
                "Uninstall Lagwise", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return 4;

        if (!CloseRunningCopy(dir))
        {
            if (!quiet) MessageBox.Show("Lagwise is still running and could not be closed. Choose Exit in its tray icon menu, then uninstall again.", "Lagwise", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        var result = installer.Uninstall(dir);
        if (!quiet) MessageBox.Show(result.Message, "Lagwise uninstalled", MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    /// <summary>Asks a running Lagwise from <paramref name="dir"/> to save and exit, then waits for it. True when none is left.</summary>
    private static bool CloseRunningCopy(string dir)
    {
        string target = Path.GetFullPath(Path.Combine(dir, SelfInstaller.ExeName));
        Process[] Running() => Process.GetProcessesByName("Lagwise").Where(p =>
        {
            if (p.Id == Environment.ProcessId) return false;
            try { return string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }).ToArray();

        var running = Running();
        if (running.Length == 0) return true;
        AppInstance.TrySend(AppInstance.KeyFor(SettingsService.DefaultDirectory()), AppInstance.Exit, TimeSpan.FromSeconds(3));
        foreach (var p in running) p.WaitForExit(20000);
        Thread.Sleep(300);
        return Running().Length == 0;
    }
}
