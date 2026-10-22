using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Lagwise.Core;
using Lagwise.Core.Games;
using Lagwise.Core.Services;

namespace Lagwise.App;

/// <summary>
/// Interaction logic for App.xaml. Owns the single <see cref="LagwiseHost"/> shared by the main window,
/// the system tray and the Game Monitor.
/// </summary>
public partial class App : System.Windows.Application
{
    public static LagwiseHost Host { get; private set; } = null!;

    /// <summary>Set while Windows signs out, restarts or shuts down: the window then closes for real instead of hiding in the tray.</summary>
    public static bool IsSessionEnding { get; private set; }

    /// <summary>Raised on the UI thread for each request a later start (a second double-click, the taskbar jump list, the installer) handed over.</summary>
    public static event Action<string>? CommandReceived;

    /// <summary>Requests that arrived before the main window was ready to act on them.</summary>
    private static readonly Queue<string> PendingCommands = new();

    /// <summary>Held while this copy runs: one Lagwise per data folder.</summary>
    private Mutex? _instance;
    private readonly CancellationTokenSource _listening = new();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    private const int AsfwAny = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless verification of a published build: Lagwise.exe --selftest [--selftest-out report.json] [--selftest-data dir]
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(RunSelfTest(e.Args));
            return;
        }

        // a second start hands its request to the copy already running for this data folder, then closes
        if (!ClaimInstance(SettingsService.DefaultDirectory(), e.Args))
        {
            Environment.Exit(0);
            return;
        }

        // first start after the rename: NetPulse's data comes along (copied; the old folder is left as it was)
        bool ownData = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SettingsService.DataDirectoryVariable));
        if (ownData) LegacyNetPulse.CopyDataIfFirstStart(LegacyNetPulse.OldDataDirectory(), SettingsService.DefaultDirectory());

        Host = new LagwiseHost();
        ErrorLog.Directory = Path.Combine(Host.DataDirectory, "logs");
        Themes.ThemeManager.Apply(Host.Settings.Theme, Host.Settings.Accent); // before the first window is built

        // sleep: save the recording in progress; waking: give the adapter time to reconnect before silence counts as a drop
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // NetPulse's startup entry becomes Lagwise's, and a moved portable copy keeps starting with Windows
        // (only the everyday copy, never a test run with its own data folder)
        if (ownData)
        {
            Host.WindowsIntegration?.MigrateStartupEntry(LegacyNetPulse.OldName);
            Host.WindowsIntegration?.RepairStartupEntry();
        }
        if (!PageSnapshots.Requested(out _, out _)) TaskbarTasks.ApplyJumpList(this);

        // An unexpected error in one button or background task must not take the whole app (and a running recording) down.
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("UI", args.Exception);
            args.Handled = true;
            ErrorLog.TellUserOnce(args.Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Background task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("Fatal", ex);
            // last chance: save whatever is being recorded
            try { Host.StopSession(out string? _); } catch { /* already failing */ }
        };

        // A table inside a scrolling page swallows the mouse wheel even when it has nothing to scroll; hand it on to the page instead.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DataGrid), UIElement.PreviewMouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler(ForwardWheel));
        base.OnStartup(e);
    }

    /// <summary>
    /// One Lagwise per data folder. When a copy already runs, the request in <paramref name="args"/> goes to it and this
    /// start ends (false). A copy that does not answer may be closing: this start waits for it, never runs beside it.
    /// </summary>
    private bool ClaimInstance(string dataDirectory, string[] args)
    {
        string key = AppInstance.KeyFor(dataDirectory);
        string command = AppInstance.CommandFromArgs(args);
        _instance = new Mutex(true, @"Local\" + key, out bool first);
        if (!first)
        {
            // the copy that gets the request may bring its window forward: Windows allows that only with the started program's leave
            AllowSetForegroundWindow(AsfwAny);
            if (AppInstance.TrySend(key, command, TimeSpan.FromSeconds(5))) return false;
            try
            {
                if (!_instance.WaitOne(TimeSpan.FromSeconds(15))) return false;
            }
            catch (AbandonedMutexException)
            {
                // the other copy ended without letting go: this one owns it now
            }
        }
        else if (command == AppInstance.Exit)
        {
            return false;   // "close Lagwise" with nothing running
        }

        _ = AppInstance.ListenAsync(key, c => Dispatcher.BeginInvoke(() => Deliver(c)), _listening.Token);
        // started from the jump list while Lagwise was closed: do what was asked once the window is up
        if (command is not (AppInstance.Show or AppInstance.None)) PendingCommands.Enqueue(command);
        return true;
    }

    private static void Deliver(string command)
    {
        if (CommandReceived != null) CommandReceived(command);
        else PendingCommands.Enqueue(command);
    }

    /// <summary>Requests that came in before the main window subscribed to <see cref="CommandReceived"/>, oldest first.</summary>
    public static IReadOnlyList<string> TakePendingCommands()
    {
        var list = PendingCommands.ToList();
        PendingCommands.Clear();
        return list;
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Host.OnSystemSuspending();
        else if (e.Mode == PowerModes.Resume) Host.OnSystemResumed();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // sign-out, restart or shutdown: WPF closes the windows and runs OnExit, which saves whatever is being recorded
        IsSessionEnding = true;
        base.OnSessionEnding(e);
    }

    private static void ForwardWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not System.Windows.Controls.DataGrid grid) return;

        System.Windows.Controls.ScrollViewer? inner = null;
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(grid);
        while (queue.Count > 0 && inner == null)
        {
            var node = queue.Dequeue();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.ScrollViewer sv) { inner = sv; break; }
                queue.Enqueue(child);
            }
        }

        bool canScroll = inner != null && inner.ScrollableHeight > 0
            && ((e.Delta > 0 && inner.VerticalOffset > 0) || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight));
        if (canScroll) return;

        if (System.Windows.Media.VisualTreeHelper.GetParent(grid) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = grid
        });
    }

    private static int RunSelfTest(string[] args)
    {
        string? ArgValue(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string dataDir = ArgValue("--selftest-data") ?? Path.Combine(Path.GetTempPath(), "lagwise-selftest-" + Guid.NewGuid().ToString("N"));
        string? outFile = ArgValue("--selftest-out");

        var report = SelfTest.Run(dataDir);
        string json = SelfTest.ToJson(report);
        if (outFile != null) File.WriteAllText(outFile, json);
        return report.Ok ? 0 : 1;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listening.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Host?.Dispose();
        // let a waiting start take over only once everything is saved
        try { _instance?.ReleaseMutex(); }
        catch (ApplicationException) { /* not held (ended before claiming it) */ }
        base.OnExit(e);
    }
}
