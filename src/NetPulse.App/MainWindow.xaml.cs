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

    public MainWindow()
    {
        // One shared service host (created in App.OnStartup) backs the window, tray and future Game Monitor.
        DataContext = new MainViewModel(App.Host);
        InitializeComponent();

        SourceInitialized += (_, _) => WindowChrome.ApplyDarkTitleBar(this);
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

            // started by the "start with Windows" entry: begin in the tray / taskbar rather than in front of the user
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
                WindowState = WindowState.Minimized;
        }
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

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Minimize to tray if minimized
        if (WindowState == WindowState.Minimized && App.Host.Settings.MinimizeToTray)
        {
            Hide();
            _trayManager?.ShowNotification("NetPulse Running in Background", "Click tray icon to restore window.", System.Windows.Forms.ToolTipIcon.Info);
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
            _trayManager?.ShowNotification("NetPulse is still running", "Open it from the tray icon. Choose Exit there to quit.", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.OnAppExiting();
        }
        _trayManager?.Dispose();
    }
}
