using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Lagwise.App.ViewModels;
using Lagwise.Core.Models;

namespace Lagwise.App.Tray;

public class SystemTrayManager : IDisposable
{
    private readonly Window _mainWindow;
    private readonly MainViewModel _viewModel;
    private NotifyIcon? _notifyIcon;
    /// <summary>Page to open when the most recent notification is clicked (null: just show the window).</summary>
    private string? _balloonTab;
    /// <summary>The plain Lagwise icon, and the same with a coloured dot for each state that has one.</summary>
    private Icon? _baseIcon;
    private readonly Dictionary<DiagnosticStatus, Icon> _statusIcons = new();
    private DiagnosticStatus _status = DiagnosticStatus.Healthy;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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
            Text = "Lagwise - Real-time Network Quality",
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
        var showItem = new ToolStripMenuItem("Show Lagwise", null, (s, e) => RestoreWindow());
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
            if (!_viewModel.ToggleGameRecording()) RestoreWindow();
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
            gameRecording.Text = gm.IsMonitoring ? $"Stop && save the {gm.RecordingGameName} recording" : "Record a game session now";
            markLag.Enabled = App.Host.IsRecording;
            overlay.Checked = _viewModel.IsOverlayVisible;
            string key = App.Host.Settings.MarkLagHotkey;
            markLag.ShortcutKeyDisplayString = key == Lagwise.Core.Models.HotkeyGesture.Off ? "" : key;
            markLag.ShowShortcutKeys = key != Lagwise.Core.Models.HotkeyGesture.Off;
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

        contextMenu.Items.Add("Exit Lagwise", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            if (_mainWindow is Lagwise.App.MainWindow main) main.ExitApplication();
            else _mainWindow.Close();
            System.Windows.Application.Current.Shutdown();
        });

        _baseIcon = _notifyIcon.Icon;
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

    /// <summary>
    /// The tray icon shows the connection at a glance: the plain logo while all is well, with an amber dot when something
    /// is worth a look and a red one when the connection has a problem.
    /// </summary>
    public void SetStatus(DiagnosticStatus status)
    {
        if (_notifyIcon == null || _baseIcon == null || status == _status) return;
        _status = status;
        try
        {
            _notifyIcon.Icon = status == DiagnosticStatus.Healthy ? _baseIcon : IconFor(status);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            _notifyIcon.Icon = _baseIcon;   // GDI+ could not draw it: the plain icon still works
        }
    }

    private Icon IconFor(DiagnosticStatus status)
    {
        if (_statusIcons.TryGetValue(status, out var made)) return made;
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var logo = new Icon(_baseIcon!, size, size)) g.DrawIcon(logo, new Rectangle(0, 0, size, size));
            float d = size * 0.56f;
            var dot = new RectangleF(size - d, size - d, d - 1, d - 1);
            using var ring = new SolidBrush(System.Drawing.Color.FromArgb(255, 16, 18, 24));
            using var fill = new SolidBrush(status == DiagnosticStatus.Critical ? System.Drawing.Color.FromArgb(0xE5, 0x48, 0x4D) : System.Drawing.Color.FromArgb(0xF5, 0x9E, 0x0B));
            g.FillEllipse(ring, RectangleF.Inflate(dot, 1.2f, 1.2f));
            g.FillEllipse(fill, dot);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            made = (Icon)temporary.Clone();   // owns a copy of the handle
        }
        finally
        {
            DestroyIcon(handle);
        }
        _statusIcons[status] = made;
        return made;
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
        foreach (var icon in _statusIcons.Values) icon.Dispose();
        _statusIcons.Clear();
    }
}
