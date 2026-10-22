using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lagwise.App;

/// <summary>Makes the Windows title bar match the dark UI (Windows 10 20H1+ / Windows 11). Silently does nothing where unsupported.</summary>
internal static class WindowStyling
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmCornerPreference = 33;
    private const int DwmBorderColor = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkTitleBar(Window window) => Apply(window, light: false);

    /// <summary>Matches the window frame (DWM dark mode, rounded corners, border colour) to the dark or light theme.</summary>
    public static void Apply(Window window, bool light)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));

            // Windows 11: colour the caption to the sidebar surface (COLORREF = 0x00BBGGRR) so the title bar blends with the app bar
            int caption = light ? 0x00FDFBFA : 0x0018110D;   // #FAFBFD / #0D1118
            int text = light ? 0x00271811 : 0x00F6F0ED;      // #111827 / #EDF0F6
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));

            // Windows 11: keep rounded corners and a subtle border with the custom (WindowChrome) title bar
            int round = 2;               // DWMWCP_ROUND
            int border = light ? 0x00EBE4E0 : 0x00392A22;     // #E0E4EB / #222A39
            DwmSetWindowAttribute(hwnd, DwmCornerPreference, ref round, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // older Windows: keep the default title bar
        }
    }
}
