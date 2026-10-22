using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Lagwise.Installer;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly bool _isUninstallMode;
    private readonly string _defaultInstallDir;

    public MainWindow()
    {
        InitializeComponent();

        _defaultInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Lagwise");

        TxtInstallDir.Text = _defaultInstallDir;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null) SubtitleBlock.Text = $"Version {version.ToString(3)} • {SubtitleBlock.Text}";

        var args = Environment.GetCommandLineArgs();
        _isUninstallMode = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("-u", StringComparison.OrdinalIgnoreCase));

        if (_isUninstallMode)
        {
            Title = "Uninstall Lagwise";
            TitleBlock.Text = "Uninstall Lagwise Diagnostics";
            SubtitleBlock.Text = "Remove Lagwise and its components from your computer.";
            InstallPanel.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Visible;
            BtnAction.Content = "Uninstall";
            TxtStatus.Text = "Ready to uninstall.";
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Lagwise Installation Folder",
            InitialDirectory = TxtInstallDir.Text
        };

        if (dialog.ShowDialog() == true)
        {
            TxtInstallDir.Text = dialog.FolderName;
        }
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (BtnAction.Content.ToString() == "Finished" || BtnAction.Content.ToString() == "Close")
        {
            Close();
            return;
        }

        if (_isUninstallMode)
        {
            await RunUninstallAsync();
        }
        else
        {
            await RunInstallAsync();
        }
    }

    private async Task RunInstallAsync()
    {
        string installDir = TxtInstallDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir)) return;

        BtnAction.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 15;
        TxtStatus.Text = "Creating installation directory...";

        await Task.Run(() =>
        {
            Directory.CreateDirectory(installDir);
        });

        ProgressBar.Value = 35;
        TxtStatus.Text = "Locating application binaries...";

        string? sourceExe = FindSourceBinary();
        if (string.IsNullOrEmpty(sourceExe) || !File.Exists(sourceExe))
        {
            MessageBox.Show(
                "Could not find Lagwise.exe binary. Please ensure Lagwise.exe is present in the installer directory or dist/portable folder.",
                "Installation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            BtnAction.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Installation failed: missing binary.";
            return;
        }

        ProgressBar.Value = 45;
        TxtStatus.Text = "Closing Lagwise if it is running...";
        if (!await CloseRunningLagwiseAsync(installDir))
        {
            StopWithMessage("Lagwise is still running from this folder and could not be closed. Choose Exit in its tray icon menu, then run the setup again.",
                "Installation failed: Lagwise is running.");
            return;
        }

        // Lagwise used to be called NetPulse: an old installation is removed so only Lagwise is left (its data stays)
        if (await Task.Run(RemoveOldNetPulse) == OldInstall.StillRunning)
            MessageBox.Show("An older NetPulse (Lagwise's former name) is still running, so it was left installed. Close it from its tray icon and run this setup again to remove it. Your settings and recordings come over to Lagwise either way.",
                "Lagwise Setup", MessageBoxButton.OK, MessageBoxImage.Information);

        ProgressBar.Value = 55;
        TxtStatus.Text = "Copying files to installation folder...";

        string targetExe = Path.Combine(installDir, "Lagwise.exe");
        string targetIco = Path.Combine(installDir, "app.ico");
        string targetSetup = Path.Combine(installDir, "Lagwise-Setup.exe");

        try
        {
            await Task.Run(() => CopyFiles(sourceExe, targetExe, targetSetup, targetIco));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StopWithMessage($"The files could not be copied: {ex.Message}", "Installation failed: files could not be copied.");
            return;
        }

        ProgressBar.Value = 75;
        TxtStatus.Text = "Creating desktop and start menu shortcuts...";
        await FinishInstallAsync(installDir, targetExe, targetSetup);
    }

    private void StopWithMessage(string message, string status)
    {
        MessageBox.Show(message, "Lagwise Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        BtnAction.IsEnabled = true;
        BtnCancel.IsEnabled = true;
        ProgressBar.Visibility = Visibility.Collapsed;
        TxtStatus.Text = status;
    }

    /// <summary>
    /// A Lagwise running from <paramref name="installDir"/> holds its program file open. It is asked to save and exit
    /// (as its tray menu's Exit does), then given time to finish. True when none is left running.
    /// </summary>
    private static async Task<bool> CloseRunningLagwiseAsync(string installDir)
    {
        string target = Path.GetFullPath(Path.Combine(installDir, "Lagwise.exe"));
        List<Process> Running() => Process.GetProcessesByName("Lagwise").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }).ToList();

        var running = Running();
        if (running.Count == 0) return true;
        string dataDir = Environment.GetEnvironmentVariable("LAGWISE_DATA_DIR") is { Length: > 0 } custom
            ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lagwise");
        await Task.Run(() => Lagwise.Core.AppInstance.TrySend(Lagwise.Core.AppInstance.KeyFor(dataDir), Lagwise.Core.AppInstance.Exit, TimeSpan.FromSeconds(3)));
        await Task.Run(() => { foreach (var p in running) p.WaitForExit(20000); });
        return Running().Count == 0;
    }

    private enum OldInstall { None, Removed, StillRunning }

    /// <summary>
    /// Removes an installation of NetPulse (Lagwise's former name): its shortcuts, its entry in Settings > Apps and its
    /// three program files. Its data folder is left alone; Lagwise copies it on first start. Versions before 1.0 cannot
    /// be asked to exit, so a running one is left in place.
    /// </summary>
    private static OldInstall RemoveOldNetPulse()
    {
        const string oldKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NetPulse";
        string? dir = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(oldKey);
            dir = key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        if (string.IsNullOrWhiteSpace(dir)) return OldInstall.None;
        dir = dir.TrimEnd('\\', '/');
        string oldExe = Path.Combine(dir, "NetPulse.exe");

        bool running = Process.GetProcessesByName("NetPulse").Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, oldExe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        });
        if (running) return OldInstall.StillRunning;

        try
        {
            foreach (var folder in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.Programs })
            {
                string link = Path.Combine(Environment.GetFolderPath(folder), "NetPulse.lnk");
                if (File.Exists(link)) File.Delete(link);
            }
            // only the files NetPulse's installer put there, and the folder if that empties it
            foreach (var name in new[] { "NetPulse.exe", "app.ico", "NetPulse-Setup.exe" })
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) File.Delete(file);
            }
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            Registry.CurrentUser.DeleteSubKeyTree(oldKey, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // a file in use or protected: the rest is still tidy, and Lagwise works regardless
        }
        return OldInstall.Removed;
    }

    private static void CopyFiles(string sourceExe, string targetExe, string targetSetup, string targetIco)
    {
        {
            File.Copy(sourceExe, targetExe, true);

            string currentProcessExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(currentProcessExe) && File.Exists(currentProcessExe) &&
                !currentProcessExe.Equals(targetSetup, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentProcessExe, targetSetup, true);
            }

            string? srcIco = Path.Combine(Path.GetDirectoryName(sourceExe) ?? "", "app.ico");
            if (File.Exists(srcIco))
            {
                File.Copy(srcIco, targetIco, true);
            }
        }
    }

    private async Task FinishInstallAsync(string installDir, string targetExe, string targetSetup)
    {

        // read UI state on the UI thread; touching WPF controls from the worker thread crashed the installer
        bool desktopShortcut = ChkDesktopShortcut.IsChecked == true;
        bool startMenuShortcut = ChkStartMenuShortcut.IsChecked == true;

        await Task.Run(() =>
        {
            if (desktopShortcut)
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                CreateShortcut(Path.Combine(desktopPath, "Lagwise.lnk"), targetExe, installDir, "Lagwise - Real-time Network Diagnostics");
            }

            if (startMenuShortcut)
            {
                string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                CreateShortcut(Path.Combine(programsPath, "Lagwise.lnk"), targetExe, installDir, "Lagwise - Real-time Network Diagnostics");
            }

            // Register with Windows Uninstall in registry
            RegisterUninstall(installDir, targetSetup, targetExe);
        });

        ProgressBar.Value = 100;
        TxtStatus.Text = "Installation completed successfully!";

        if (ChkLaunchAfter.IsChecked == true)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        BtnAction.Content = "Finished";
        BtnAction.IsEnabled = true;
        BtnCancel.Visibility = Visibility.Collapsed;
    }

    private async Task RunUninstallAsync()
    {
        BtnAction.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 30;
        TxtStatus.Text = "Removing shortcuts and registry entries...";

        // Only ever remove the registered Lagwise installation, never "whatever folder this setup happens to run from".
        string? registered = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise");
            registered = key?.GetValue("InstallLocation") as string;
        }
        catch { }

        string installDir = (!string.IsNullOrWhiteSpace(registered) ? registered : AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\', '/');
        bool looksLikeInstall = File.Exists(Path.Combine(installDir, "Lagwise.exe")) && File.Exists(Path.Combine(installDir, "Lagwise-Setup.exe"));

        // a running Lagwise saves what it is recording and exits, so its files can be removed
        TxtStatus.Text = "Closing Lagwise if it is running...";
        if (!await CloseRunningLagwiseAsync(installDir))
        {
            StopWithMessage("Lagwise is still running and could not be closed. Choose Exit in its tray icon menu, then uninstall again.", "Uninstall stopped: Lagwise is running.");
            return;
        }

        await Task.Run(() =>
        {
            // Remove Shortcuts
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Lagwise.lnk");
            if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

            string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lagwise.lnk");
            if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

            // Remove Registry
            UnregisterUninstall();
            // the "start with Windows" entry, when it starts this copy: Windows would otherwise try to start a deleted program
            new Lagwise.Core.Services.WindowsIntegrationService().RemoveStartupEntryFor(installDir);
        });

        ProgressBar.Value = 80;
        TxtStatus.Text = "Cleaning up installation files...";

        // Self-deletion cmd script: removes only the application files this installer created, and the folder if that leaves it empty.
        // User data (settings, recordings) lives in %LOCALAPPDATA%\Lagwise and is intentionally left alone.
        // the folder name goes into a command line: refuse quotes and the characters cmd.exe treats specially
        bool safeName = installDir.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n' }) < 0 && Path.IsPathFullyQualified(installDir);
        if (looksLikeInstall && safeName)
        {
            string cmdScript = $"/c timeout /t 2 /nobreak > NUL & del /f /q \"{installDir}\\Lagwise.exe\" \"{installDir}\\app.ico\" \"{installDir}\\Lagwise-Setup.exe\" & rmdir \"{installDir}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),   // never by name: a cmd.exe beside the setup would run instead
                Arguments = cmdScript,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        ProgressBar.Value = 100;
        TxtStatus.Text = "Lagwise has been uninstalled.";

        MessageBox.Show("Lagwise has been successfully removed from your computer.", "Lagwise Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private static string? FindSourceBinary()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(baseDir, "Lagwise.exe"),
            Path.Combine(baseDir, "payload", "Lagwise.exe"),
            Path.Combine(baseDir, "portable", "Lagwise.exe"),
            Path.Combine(baseDir, "..", "dist", "portable", "Lagwise.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "dist", "portable", "Lagwise.exe")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return null;
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetPath;
                    shortcut.WorkingDirectory = workingDir;
                    shortcut.Description = description;
                    shortcut.Save();
                }
            }
        }
        catch { }
    }

    private static void RegisterUninstall(string installDir, string setupExe, string appExe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise");
            if (key != null)
            {
                key.SetValue("DisplayName", "Lagwise Real-Time Network Quality & Path Diagnostics");
                key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(appExe).ProductVersion?.Split('+')[0] ?? "");
                key.SetValue("Publisher", "Lagwise Diagnostics");
                key.SetValue("DisplayIcon", $"{appExe},0");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", $"\"{setupExe}\" --uninstall");
                key.SetValue("EstimatedSize", 175000, RegistryValueKind.DWord); // in KB
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
                // Settings > Apps offers only Uninstall: there is nothing to modify or repair
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    private static void UnregisterUninstall()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise", false);
        }
        catch { }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}