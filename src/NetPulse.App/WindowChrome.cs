using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetPulse.App;

/// <summary>Makes the Windows title bar match the dark UI (Windows 10 20H1+ / Windows 11). Silently does nothing where unsupported.</summary>
internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));

            // Windows 11: colour the caption to the sidebar surface (COLORREF = 0x00BBGGRR) so the title bar blends with the app bar
            int caption = 0x0018110D;   // #0D1118
            int text = 0x00F6F0ED;      // #EDF0F6
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // older Windows: keep the default title bar
        }
    }
}
