using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lagwise.App.Tray;
using Lagwise.App.ViewModels;

namespace Lagwise.App;

public partial class MainWindow : Window
{
    // DataGrid columns that only make sense in Advanced mode, matched by header text.
    private static readonly HashSet<string> AdvancedTargetColumns = new() { "Protocol", "Min / Max", "Jitter", "PID", "Local endpoint", "95th pct", "Max", "Probes" };
    private static readonly HashSet<string> AdvancedRouteColumns = new() { "Best", "Worst", "ASN / Organization", "Diagnostics" };

    private SystemTrayManager? _trayManager;
    private GlobalHotkey? _hotkey;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;
    private bool _toldAboutTray;
    private OverlayWindow? _overlay;
    private readonly Lagwise.Core.Services.AlertPolicy _alerts = new(DateTime.UtcNow);

    public MainWindow()
    {
        // One shared service host (created in App.OnStartup) backs the window, tray and future Game Monitor.
        DataContext = new MainViewModel(App.Host);
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowStyling.Apply(this, Lagwise.App.Themes.ThemeManager.IsLight);
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
            {
                _hotkey = new GlobalHotkey(source, OnMarkLagShortcut);
                ApplyHotkey();
                source.AddHook(SnapLayoutHook);
            }
        };
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
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
            vm.Appearance.Applied += (_, _) => { ApplyAppearance(); _overlay?.ApplyOpacity(); _overlay?.ApplyClickThrough(); };
            vm.OverlayVisibilityChanged += (_, _) => SyncOverlay(vm);
            SyncOverlay(vm);
            Lagwise.App.Themes.ThemeManager.Changed += (_, _) => WindowStyling.Apply(this, Lagwise.App.Themes.ThemeManager.IsLight);
            ApplyAppearance();
            App.Host.Events.EventLogged += (_, evt) => Dispatcher.InvokeAsync(() => MaybeAlert(evt));
            App.Host.GameMonitor.GameLaunched += (_, game) => Dispatcher.InvokeAsync(() => ShowGameStartCheck(game));

            // the taskbar button: a badge for trouble or a recording, progress while a test runs, and buttons under the preview
            SetUpTaskbar(vm);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.StatusLevel) or nameof(MainViewModel.IsRecording)) UpdateStatusMarks(vm);
                if (args.PropertyName is nameof(MainViewModel.IsSpeedTestRunning) or nameof(MainViewModel.SpeedTestProgress)
                    or nameof(MainViewModel.IsBufferbloatRunning) or nameof(MainViewModel.BufferbloatProgress)) UpdateTaskbarProgress(vm);
            };
            vm.GameMonitor.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(GameMonitorViewModel.IsMonitoring)) UpdateStatusMarks(vm); };
            UpdateStatusMarks(vm);

            // requests from a second start, the taskbar jump list or the installer, including any that came in while starting up
            App.CommandReceived += HandleCommand;
            foreach (var command in App.TakePendingCommands()) HandleCommand(command);

            // tray hover text: a glance at the connection without opening the window
            _trayTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateTrayTooltip(vm), Dispatcher);
            UpdateTrayTooltip(vm);

            // started by the "start with Windows" entry: begin in the tray / taskbar rather than in front of the user
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase) || StartupTaskBackend.StartedAtSignIn())
                WindowState = WindowState.Minimized;

            // documentation and look checks: render pages to PNG files, then close
            if (PageSnapshots.Requested(out var spec, out int wait)) _ = PageSnapshots.RunAsync(this, vm, spec, wait);
        }
    }

    /// <summary>A request handed over by a later start of Lagwise.exe (see <see cref="Lagwise.Core.AppInstance"/>).</summary>
    public void HandleCommand(string command)
    {
        if (DataContext is not MainViewModel vm) return;
        switch (command)
        {
            case Lagwise.Core.AppInstance.Show:
                ShowFromElsewhere();
                break;
            case Lagwise.Core.AppInstance.MarkLag:
                OnMarkLagShortcut();
                break;
            case Lagwise.Core.AppInstance.Record:
                if (!vm.ToggleGameRecording()) ShowFromElsewhere();
                break;
            case Lagwise.Core.AppInstance.Overlay:
                vm.IsOverlayVisible = !vm.IsOverlayVisible;
                break;
            case Lagwise.Core.AppInstance.SpeedTest:
                ShowFromElsewhere();
                vm.CurrentTab = "SpeedTest";
                if (vm.RunSpeedTestCommand.CanExecute(null)) vm.RunSpeedTestCommand.Execute(null);
                break;
            case Lagwise.Core.AppInstance.Bufferbloat:
                ShowFromElsewhere();
                vm.CurrentTab = "Bufferbloat";
                if (vm.RunBufferbloatCommand.CanExecute(null)) vm.RunBufferbloatCommand.Execute(null);
                break;
            case Lagwise.Core.AppInstance.History:
                ShowFromElsewhere();
                vm.CurrentTab = "History";
                break;
            case Lagwise.Core.AppInstance.Exit:
                ExitApplication();
                System.Windows.Application.Current.Shutdown();
                break;
        }
    }

    /// <summary>Brings the window back from the tray or the taskbar and in front of other windows.</summary>
    private void ShowFromElsewhere()
    {
        if (_trayManager != null) _trayManager.RestoreWindow();
        else { Show(); WindowState = WindowState.Normal; Activate(); }
    }

    // ---- taskbar button

    private System.Windows.Shell.ThumbButtonInfo? _thumbRecord;

    private void SetUpTaskbar(MainViewModel vm)
    {
        var info = new System.Windows.Shell.TaskbarItemInfo();
        var markLag = new System.Windows.Shell.ThumbButtonInfo { ImageSource = TaskbarTasks.Glyph("\uE7C1"), Description = "Mark lag", DismissWhenClicked = false };
        markLag.Click += (_, _) => OnMarkLagShortcut();
        _thumbRecord = new System.Windows.Shell.ThumbButtonInfo { DismissWhenClicked = false };
        _thumbRecord.Click += (_, _) => { if (!vm.ToggleGameRecording()) ShowFromElsewhere(); };
        info.ThumbButtonInfos.Add(markLag);
        info.ThumbButtonInfos.Add(_thumbRecord);
        TaskbarItemInfo = info;
    }

    /// <summary>The badge on the taskbar button and the tray icon follow the connection status and the recording.</summary>
    private void UpdateStatusMarks(MainViewModel vm)
    {
        bool recording = App.Host.IsRecording;
        if (TaskbarItemInfo != null)
        {
            var (image, text) = TaskbarTasks.Badge(vm.StatusLevel, recording);
            TaskbarItemInfo.Overlay = image;
            TaskbarItemInfo.Description = text.Length == 0 ? "Lagwise" : $"Lagwise - {text}";
        }
        if (_thumbRecord != null)
        {
            bool gameRecording = vm.GameMonitor.IsMonitoring;
            _thumbRecord.ImageSource = TaskbarTasks.Glyph(gameRecording ? "\uE71A" : "\uE7C8");
            _thumbRecord.Description = gameRecording ? $"Stop and save the {vm.GameMonitor.RecordingGameName} recording" : "Record a game session now";
        }
        _trayManager?.SetStatus(vm.StatusLevel);
    }

    private void UpdateTaskbarProgress(MainViewModel vm)
    {
        if (TaskbarItemInfo == null) return;
        int? percent = vm.IsSpeedTestRunning ? vm.SpeedTestProgress : vm.IsBufferbloatRunning ? vm.BufferbloatProgress : null;
        TaskbarItemInfo.ProgressState = percent.HasValue ? System.Windows.Shell.TaskbarItemProgressState.Normal : System.Windows.Shell.TaskbarItemProgressState.None;
        TaskbarItemInfo.ProgressValue = Math.Clamp((percent ?? 0) / 100.0, 0, 1);
    }

    // ---- Windows 11 Snap Layouts: hovering the maximise button offers the layouts, as on Windows' own title bars

    private const int WmNcHitTest = 0x0084, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmNcMouseLeave = 0x02A2, HtMaxButton = 9;
    private bool _maxPressed;

    /// <summary>
    /// The title bar is drawn by Lagwise (WindowChrome), so Windows only offers Snap Layouts when told the pointer is on
    /// the maximise button. Clicks there then arrive as non-client messages and are handled here.
    /// </summary>
    private IntPtr SnapLayoutHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                bool over = IsOverMaximize(lParam);
                MaximizeButton.Tag = over ? "hover" : null;
                if (over) { handled = true; return new IntPtr(HtMaxButton); }
                break;
            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                _maxPressed = true;
                handled = true;
                break;
            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                if (_maxPressed) MaximizeButton_Click(MaximizeButton, new RoutedEventArgs());
                _maxPressed = false;
                handled = true;
                break;
            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                _maxPressed = false;
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) == null) return false;
        long v = lParam.ToInt64();
        var screen = new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        try
        {
            var p = MaximizeButton.PointFromScreen(screen);
            return p.X >= 0 && p.Y >= 0 && p.X < MaximizeButton.ActualWidth && p.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Keyboard shortcuts (listed on the Help page).</summary>
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        const System.Windows.Input.ModifierKeys ctrl = System.Windows.Input.ModifierKeys.Control;

        if (mods == ctrl && key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
        {
            int n = key == System.Windows.Input.Key.D0 ? 9 : key - System.Windows.Input.Key.D1;
            e.Handled = vm.GoToPage(n);
        }
        else if (mods == ctrl && key >= System.Windows.Input.Key.NumPad0 && key <= System.Windows.Input.Key.NumPad9)
        {
            int n = key == System.Windows.Input.Key.NumPad0 ? 9 : key - System.Windows.Input.Key.NumPad1;
            e.Handled = vm.GoToPage(n);
        }
        else if (mods == ctrl && key == System.Windows.Input.Key.OemComma) { vm.CurrentTab = "Settings"; e.Handled = true; }
        else if (mods == ctrl && key == System.Windows.Input.Key.H) { vm.CurrentTab = "History"; e.Handled = true; }
        else if (mods == ctrl && key == System.Windows.Input.Key.D) { vm.CurrentTab = "Diary"; e.Handled = true; }
        else if (mods == ctrl && key == System.Windows.Input.Key.K) { vm.CurrentTab = "Sites"; e.Handled = true; }
        else if (mods == System.Windows.Input.ModifierKeys.None && key == System.Windows.Input.Key.F1) { vm.CurrentTab = "Help"; e.Handled = true; }
        else if (mods == ctrl && key == System.Windows.Input.Key.E) { vm.ExportReportCommand.Execute(null); e.Handled = true; }
        else if (mods == (ctrl | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.C) { vm.CopySummaryCommand.Execute(null); e.Handled = true; }
        else if (mods == (ctrl | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.O) { vm.ToggleOverlayCommand.Execute(null); e.Handled = true; }
        else if (mods == ctrl && key == System.Windows.Input.Key.T) { vm.Appearance.ToggleThemeCommand.Execute(null); e.Handled = true; }
    }

    private void SyncOverlay(MainViewModel vm)
    {
        if (vm.IsOverlayVisible)
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow(vm);
                _overlay.HideRequested += (_, _) => vm.IsOverlayVisible = false;
            }
            _overlay.Show();
        }
        else
        {
            _overlay?.Hide();
        }
    }

    /// <summary>Interface size: the whole window content is scaled, and the draggable title area grows with it.</summary>
    private void ApplyAppearance()
    {
        double scale = App.Host.Settings.UiScalePercent / 100.0;
        RootGrid.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(scale, scale);
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.CaptionHeight = 48 * scale;
        WindowStyling.Apply(this, Lagwise.App.Themes.ThemeManager.IsLight);
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

    /// <summary>
    /// The selected game just started: one notification with the "Ready to play?" verdict, so a problem on your side is
    /// known before the match, not halfway through it. Clicking it opens the Overview.
    /// </summary>
    private void ShowGameStartCheck(Lagwise.Core.Games.GameProfile game)
    {
        var settings = App.Host.Settings;
        if (!settings.DesktopAlerts || !settings.GameStartCheck || _trayManager == null || DataContext is not MainViewModel vm) return;
        if (settings.AlertsOnlyInBackground && IsVisible && IsActive && WindowState != WindowState.Minimized) return;
        var (title, message, problem) = Lagwise.Core.Services.ReadinessCheck.GameStartNotice(game.DisplayName, vm.EvaluateReadiness());
        _trayManager.ShowNotification(title, message, problem ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, "Dashboard");
    }

    /// <summary>Desktop notification for the few events that deserve one (see AlertPolicy); clicking it opens the timeline.</summary>
    private void MaybeAlert(Lagwise.Core.Models.ConnectionEvent evt)
    {
        var settings = App.Host.Settings;
        if (!settings.DesktopAlerts || _trayManager == null) return;
        // checked before the policy, so an alert never shown is never "closed" by a Back online alert either
        if (settings.AlertsOnlyInBackground && IsVisible && IsActive && WindowState != WindowState.Minimized) return;
        var alert = _alerts.Evaluate(evt);
        if (alert == null) return;
        _trayManager.ShowNotification(alert.Title, alert.Message,
            alert.IsProblem ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, "Events");
    }

    private void UpdateTrayTooltip(MainViewModel vm)
    {
        string state = vm.GameMonitor.IsMonitoring ? $"Recording {vm.GameMonitor.RecordingGameName}" : App.Host.IsRecording ? "Recording" : "Monitoring";
        _trayManager?.SetTooltip($"Lagwise - {state}\nReference ping {vm.QuickInternetLatency:0} ms, variation {vm.QuickJitter:0} ms, probe loss {vm.QuickPacketLoss:0.#}%\n{vm.StatusCategory}");
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
            if (!_toldAboutTray) _trayManager?.ShowNotification("Lagwise is still running", "It keeps measuring in the tray. Double-click the icon to open it again.", System.Windows.Forms.ToolTipIcon.Info);
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
        if (!_exiting && !App.IsSessionEnding && App.Host.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray) _trayManager?.ShowNotification("Lagwise is still running", "Open it from the tray icon. Choose Exit there to quit.", System.Windows.Forms.ToolTipIcon.Info);
            _toldAboutTray = true;
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.OnAppExiting();
        }
        App.CommandReceived -= HandleCommand;
        _trayTimer?.Stop();
        _overlay?.Close();
        _hotkey?.Dispose();
        _trayManager?.Dispose();
    }
}
