using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using Lagwise.Core;
using Lagwise.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;

namespace Lagwise.App;

/// <summary>
/// Lagwise on the Windows taskbar: the jump list (right-click the taskbar button or the Start menu entry), the small
/// badge on the button when the connection has trouble or a recording runs, and the icons of the buttons under the
/// window preview.
/// </summary>
internal static class TaskbarTasks
{
    /// <summary>Tasks that start <c>Lagwise.exe --command ...</c>; a running Lagwise receives them (see <see cref="AppInstance"/>).</summary>
    public static void ApplyJumpList(Application app)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        void Add(string title, string description, string command) => list.JumpItems.Add(new JumpTask
        {
            Title = title,
            Description = description,
            ApplicationPath = exe,
            Arguments = "--command " + command,
            IconResourcePath = exe,
            IconResourceIndex = 0
        });
        Add("Mark lag", "Note a moment of lag in the recording in progress", AppInstance.MarkLag);
        Add("Start or stop a game recording", "Record your connection while you play, or stop and save the recording", AppInstance.Record);
        Add("Mini overlay", "Show or hide the live numbers on top of other windows", AppInstance.Overlay);
        Add("Speed test", "Measure download and upload speed", AppInstance.SpeedTest);
        Add("Bufferbloat test", "Check whether ping rises when the connection is busy", AppInstance.Bufferbloat);
        Add("History & reports", "Open your recordings", AppInstance.History);
        try
        {
            JumpList.SetJumpList(app, list);
            list.Apply();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // a locked-down Start menu: the tasks are a convenience, the tray menu has them too
        }
    }

    private static readonly Color Amber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>The badge on the taskbar button: trouble first, then a recording in progress; null for none.</summary>
    public static (ImageSource? Image, string Text) Badge(DiagnosticStatus status, bool recording) => status switch
    {
        DiagnosticStatus.Critical => (Mark(Red, "!"), "Connection problem"),
        DiagnosticStatus.Degraded => (Mark(Amber, "!"), "Connection worth a look"),
        _ when recording => (RecordDot(), "Recording"),
        _ => (null, "")
    };

    private static ImageSource Mark(Color fill, string text)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(fill), new Pen(Brushes.White, 1.2), new EllipseGeometry(new Point(8, 8), 7.2, 7.2)));
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 11, Brushes.White, 1.0);
        group.Children.Add(new GeometryDrawing(Brushes.White, null, ft.BuildGeometry(new Point(8 - ft.Width / 2, 8 - ft.Height / 2))));
        return Freeze(new DrawingImage(group));
    }

    private static ImageSource RecordDot()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(8, 8), 7.5, 7.5)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Red), null, new EllipseGeometry(new Point(8, 8), 5.5, 5.5)));
        return Freeze(new DrawingImage(group));
    }

    /// <summary>A Segoe Fluent Icons glyph as an image, in the colour that reads on the taskbar's own theme (not Lagwise's).</summary>
    public static ImageSource Glyph(string glyph)
    {
        var color = TaskbarIsLight() ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, new SolidColorBrush(color), 1.0);
        var group = new DrawingGroup();
        // a fixed 16 x 16 frame, so every button's glyph has the same size and position
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, ft.BuildGeometry(new Point((16 - ft.Width) / 2, (16 - ft.Height) / 2))));
        return Freeze(new DrawingImage(group));
    }

    /// <summary>Windows mode (taskbar and Start), which can differ from the app mode Lagwise's own theme follows.</summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ImageSource Freeze(Freezable f)
    {
        f.Freeze();
        return (ImageSource)f;
    }
}
