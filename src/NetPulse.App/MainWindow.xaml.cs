using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NetPulse.App.Tray;
using NetPulse.App.ViewModels;

namespace NetPulse.App;

public partial class MainWindow : Window
{
    // DataGrid columns that only make sense in Advanced mode, matched by header text.
    private static readonly HashSet<string> AdvancedTargetColumns = new() { "Protocol", "Min / Max", "Jitter (RFC3550)", "PID", "Local endpoint" };
    private static readonly HashSet<string> AdvancedRouteColumns = new() { "Best (ms)", "Worst (ms)", "ASN / Organization", "Diagnostics" };

    private SystemTrayManager? _trayManager;
    private GlobalHotkey? _hotkey;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;
    private bool _toldAboutTray;

    public MainWindow()
    {
        // One shared service host (created in App.OnStartup) backs the window, tray and future Game Monitor.
        DataContext = new MainViewModel(App.Host);
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowStyling.ApplyDarkTitleBar(this);
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
            {
                _hotkey = new GlobalHotkey(source, OnMarkLagShortcut);
                ApplyHotkey();
            }
        };
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _trayManager = new SystemTrayManager(this, vm);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsAdvancedMode)) ApplyMode(vm.IsAdvancedMode);
                // tables on a tab that has never been shown have no columns materialised yet, so re-apply once the tab is visible
                if (args.PropertyName == nameof(MainViewModel.CurrentTab))
                    Dispatcher.BeginInvoke(new Action(() => ApplyMode(vm.IsAdvancedMode)), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            ApplyMode(vm.IsAdvancedMode);
            vm.Settings.Applied += (_, _) => ApplyHotkey();

            // tray hover text: a glance at the connection without opening the window
            _trayTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateTrayTooltip(vm), Dispatcher);
            UpdateTrayTooltip(vm);

            // started by the "start with Windows" entry: begin in the tray / taskbar rather than in front of the user
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                WindowState = WindowState.Minimized;
        }
    }

    private void ApplyHotkey()
    {
        if (_hotkey == null || DataContext is not MainViewModel vm) return;
        var error = _hotkey.Set(App.Host.Settings.MarkLagHotkey);
        vm.Settings.HotkeyStatus = error ?? (_hotkey.Current == null ? "No shortcut is set." : $"{_hotkey.Current.Display} is active, also while a game has focus.");
        vm.GameMonitor.NotifyShortcutChanged();
    }

    private void OnMarkLagShortcut()
    {
        if (DataContext is not MainViewModel vm) return;
        bool ok = vm.MarkLagFromShortcut();
        if (!App.Host.Settings.MarkLagSound) return;
        // a short confirmation the player can hear in game; a different sound when nothing is being recorded
        if (ok) System.Media.SystemSounds.Asterisk.Play();
        else System.Media.SystemSounds.Hand.Play();
    }

    private void UpdateTrayTooltip(MainViewModel vm)
    {
        string state = vm.GameMonitor.IsMonitoring ? $"Recording {vm.GameMonitor.SelectedProfile?.Profile.DisplayName}" : App.Host.IsRecording ? "Recording" : "Monitoring";
        _trayManager?.SetTooltip($"NetPulse - {state}\nReference ping {vm.QuickInternetLatency:0} ms, variation {vm.QuickJitter:0} ms, probe loss {vm.QuickPacketLoss:0.#}%\n{vm.StatusCategory}");
    }

    private void ApplyMode(bool advanced)
    {
        foreach (var grid in FindVisualChildren<System.Windows.Controls.DataGrid>(this))
        {
            foreach (var col in grid.Columns)
            {
                string header = col.Header?.ToString() ?? string.Empty;
                if (AdvancedTargetColumns.Contains(header) || AdvancedRouteColumns.Contains(header))
                {
                    col.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    // ---- custom title bar buttons (the window uses WindowChrome, so Windows does not draw them)

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A maximised WindowChrome window extends past the screen by the resize border, so pad it back in,
    /// and swap the maximise glyph for "restore".
    /// </summary>
    private void UpdateMaximizedLayout()
    {
        bool max = WindowState == WindowState.Maximized;
        var b = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = max ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : new Thickness(0);
        MaximizeButton.Content = max ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = max ? "Restore" : "Maximise";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, max ? "Restore" : "Maximise");
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizedLayout();
        // Minimize to tray if minimized
        if (WindowState == WindowState.Minimized && App.Host.Settings.MinimizeToTray)
        {
            Hide();
            // say it once per run, not on every minimise
            if (!_toldAboutTray) _trayManager?.ShowNotification("NetPulse is still running", "It keeps measuring in the tray. Double-click the icon to open it again.", System.Windows.Forms.ToolTipIcon.Info);
            _toldAboutTray = true;
        }
    }

    /// <summary>Set when the user chose Exit from the tray menu, so "keep running in the tray" does not swallow the close.</summary>
    private bool _exiting;

    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && App.Host.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray) _trayManager?.ShowNotification("NetPulse is still running", "Open it from the tray icon. Choose Exit there to quit.", System.Windows.Forms.ToolTipIcon.Info);
            _toldAboutTray = true;
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.OnAppExiting();
        }
        _trayTimer?.Stop();
        _hotkey?.Dispose();
        _trayManager?.Dispose();
    }
}
