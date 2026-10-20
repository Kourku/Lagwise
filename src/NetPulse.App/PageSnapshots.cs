using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetPulse.App.ViewModels;

namespace NetPulse.App;

/// <summary>
/// For documentation and checking the look of a build: <c>NetPulse.exe --screenshot "GameMonitor=gm.png;History=h.png"
/// [--screenshot-wait 10]</c> ("History:&lt;recording id&gt;=h.png" opens a recording) opens each page in turn, renders the whole page (also what is scrolled out of view) to a PNG
/// file and closes. Rendering happens in software, so it works without a screen (a sleeping monitor, a build machine).
/// </summary>
public static class PageSnapshots
{
    public static bool Requested(out string spec, out int waitSeconds)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        int w = Array.FindIndex(args, a => a.Equals("--screenshot-wait", StringComparison.OrdinalIgnoreCase));
        spec = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        waitSeconds = w >= 0 && w + 1 < args.Length && int.TryParse(args[w + 1], out int s) ? Math.Clamp(s, 0, 600) : 8;
        return spec.Length > 0;
    }

    public static async Task RunAsync(Window window, MainViewModel vm, string spec, int waitSeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        foreach (var (tab, file) in Parse(spec))
        {
            try
            {
                // "History:<recording id>" opens that recording
                var parts = tab.Split(':', 2);
                if (parts.Length == 2 && parts[0] == "History") vm.OpenRecording(parts[1]);
                else vm.CurrentTab = tab;
                await Task.Delay(TimeSpan.FromSeconds(4));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Save(Render(window), file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                ErrorLog.Write("Screenshot " + tab, ex);
            }
        }
        Application.Current.Shutdown();
    }

    private static IEnumerable<(string Tab, string File)> Parse(string spec) =>
        spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => (p[0], Path.GetFullPath(p[1])));

    /// <summary>The page on show, whole: its scrolling content at full height, else the window's content.</summary>
    private static BitmapSource Render(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var page = Descendants(root).OfType<ScrollViewer>()
            .Where(s => s.IsVisible && s.ActualWidth > 600 && s.Content is FrameworkElement { ActualHeight: > 10 })
            .OrderByDescending(s => s.ActualWidth).ThenByDescending(s => ((FrameworkElement)s.Content).ActualHeight).FirstOrDefault();
        var target = page?.Content as FrameworkElement ?? root;
        double w = Math.Max(1, target.ActualWidth), h = Math.Max(1, target.ActualHeight);
        const double scale = 1.25;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? System.Windows.Media.Brushes.Black, null, new Rect(0, 0, w + 48, h + 48));
            dc.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(24, 24, w, h));
        }
        var bmp = new RenderTargetBitmap((int)((w + 48) * scale), (int)((h + 48) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void Save(BitmapSource image, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(file);
        encoder.Save(fs);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
