using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using NetPulse.App.ViewModels;

namespace NetPulse.App.Tray;

public class SystemTrayManager : IDisposable
{
    private readonly Window _mainWindow;
    private readonly MainViewModel _viewModel;
    private NotifyIcon? _notifyIcon;
    /// <summary>Page to open when the most recent notification is clicked (null: just show the window).</summary>
    private string? _balloonTab;

    public SystemTrayManager(Window mainWindow, MainViewModel viewModel)
    {
        _mainWindow = mainWindow;
        _viewModel = viewModel;
        InitializeTray();
    }

    private void InitializeTray()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "NetPulse - Real-time Network Quality",
            Visible = true
        };

        try
        {
            var iconUri = new Uri("pack://application:,,,/app.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
            if (streamInfo != null)
            {
                using var s = streamInfo.Stream;
                _notifyIcon.Icon = new Icon(s);
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        var contextMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show NetPulse", null, (s, e) => RestoreWindow());
        showItem.Font = new Font(showItem.Font, System.Drawing.FontStyle.Bold);
        contextMenu.Items.Add(showItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var markLag = new ToolStripMenuItem("Mark lag", null, (s, e) =>
        {
            if (!_viewModel.MarkLagFromShortcut())
                ShowNotification("Nothing is being recorded", "Start the Game Monitor or a recording first, then mark lag.", ToolTipIcon.Info);
        });
        var gameRecording = new ToolStripMenuItem("Start Game Monitor", null, (s, e) =>
        {
            var gm = _viewModel.GameMonitor;
            if (gm.IsMonitoring) gm.StopCommand.Execute(null);
            else if (gm.StartCommand.CanExecute(null)) gm.StartCommand.Execute(null);
            else
            {
                RestoreWindow();
                _viewModel.CurrentTab = "GameMonitor";
            }
        });
        var overlay = new ToolStripMenuItem("Mini overlay", null, (s, e) => _viewModel.IsOverlayVisible = !_viewModel.IsOverlayVisible);
        contextMenu.Items.Add(markLag);
        contextMenu.Items.Add(gameRecording);
        contextMenu.Items.Add(overlay);
        contextMenu.Items.Add("Open History && reports", null, (s, e) =>
        {
            RestoreWindow();
            _viewModel.CurrentTab = "History";
        });
        contextMenu.Opening += (_, _) =>
        {
            var gm = _viewModel.GameMonitor;
            string game = gm.SelectedProfile?.Profile.DisplayName ?? "a game";
            gameRecording.Text = gm.IsMonitoring ? $"Stop && save {game} recording" : gm.SelectedProfile == null ? "Choose a game to monitor..." : $"Start monitoring {game}";
            markLag.Enabled = App.Host.IsRecording;
            overlay.Checked = _viewModel.IsOverlayVisible;
            string key = App.Host.Settings.MarkLagHotkey;
            markLag.ShortcutKeyDisplayString = key == NetPulse.Core.Models.HotkeyGesture.Off ? "" : key;
            markLag.ShowShortcutKeys = key != NetPulse.Core.Models.HotkeyGesture.Off;
        };

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add("Run Bufferbloat Test", null, (s, e) =>
        {
            RestoreWindow();
            _viewModel.CurrentTab = "Bufferbloat";
            _viewModel.RunBufferbloatCommand.Execute(null);
        });

        contextMenu.Items.Add("Run Speed Test", null, (s, e) =>
        {
            RestoreWindow();
            _viewModel.CurrentTab = "SpeedTest";
            _viewModel.RunSpeedTestCommand.Execute(null);
        });

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add("Exit NetPulse", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            if (_mainWindow is NetPulse.App.MainWindow main) main.ExitApplication();
            else _mainWindow.Close();
            System.Windows.Application.Current.Shutdown();
        });

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
        _notifyIcon.BalloonTipClicked += (s, e) =>
        {
            RestoreWindow();
            if (_balloonTab != null) _viewModel.CurrentTab = _balloonTab;
        };
    }

    public void RestoreWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Hover text of the tray icon; Windows limits it to 127 characters.</summary>
    public void SetTooltip(string text)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Text = text.Length > 127 ? text[..126] + "\u2026" : text;
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, string? openTab = null)
    {
        if (_notifyIcon != null && _notifyIcon.Visible)
        {
            _balloonTab = openTab;
            _notifyIcon.ShowBalloonTip(5000, title, message, icon);
        }
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
