using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Lagwise.App;

/// <summary>
/// Compact always-on-top read-out of the live numbers. It never takes focus from the game (ShowActivated=False) and
/// remembers its position and background opacity in the settings.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Raised when the user hides the overlay with its own close button.</summary>
    public event EventHandler? HideRequested;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public OverlayWindow(object dataContext)
    {
        InitializeComponent();
        DataContext = dataContext;
        ApplyOpacity();
        SourceInitialized += (_, _) => ApplyClickThrough();

        var s = App.Host.Settings;
        if (s.OverlayLeft is double left && s.OverlayTop is double top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            // first use: top-right corner of the main screen, clear of most game HUDs' centre
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Top + 24;
        }
    }

    public void ApplyOpacity() => Backdrop.Opacity = Math.Clamp(App.Host.Settings.OverlayOpacityPercent, 40, 100) / 100.0;

    /// <summary>
    /// Clicking or dragging the overlay never takes the keyboard from the game (a borderless game that loses focus may
    /// minimise or stop reading keys), and it stays out of Alt+Tab. With "clicks pass through" on, the mouse reaches the
    /// game beneath it as if the overlay were not there.
    /// </summary>
    public void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow;
        style = App.Host.Settings.OverlayClickThrough ? style | WsExTransparent : style & ~(long)WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 20 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        App.Host.Settings.OverlayLeft = Left;
        App.Host.Settings.OverlayTop = Top;
        App.Host.SaveSettings(out _);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);
}
