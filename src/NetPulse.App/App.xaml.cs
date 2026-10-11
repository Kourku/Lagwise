using System;
using System.IO;
using System.Linq;
using System.Windows;
using NetPulse.Core.Games;
using NetPulse.Core.Services;

namespace NetPulse.App;

/// <summary>
/// Interaction logic for App.xaml. Owns the single <see cref="NetPulseHost"/> shared by the main window,
/// the system tray and the Game Monitor.
/// </summary>
public partial class App : System.Windows.Application
{
    public static NetPulseHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless verification of a published build: NetPulse.exe --selftest [--selftest-out report.json] [--selftest-data dir]
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(RunSelfTest(e.Args));
            return;
        }

        Host = new NetPulseHost();

        // A table inside a scrolling page swallows the mouse wheel even when it has nothing to scroll; hand it on to the page instead.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DataGrid), UIElement.PreviewMouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler(ForwardWheel));
        base.OnStartup(e);
    }

    private static void ForwardWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not System.Windows.Controls.DataGrid grid) return;

        System.Windows.Controls.ScrollViewer? inner = null;
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(grid);
        while (queue.Count > 0 && inner == null)
        {
            var node = queue.Dequeue();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.ScrollViewer sv) { inner = sv; break; }
                queue.Enqueue(child);
            }
        }

        bool canScroll = inner != null && inner.ScrollableHeight > 0
            && ((e.Delta > 0 && inner.VerticalOffset > 0) || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight));
        if (canScroll) return;

        if (System.Windows.Media.VisualTreeHelper.GetParent(grid) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = grid
        });
    }

    private static int RunSelfTest(string[] args)
    {
        string? ArgValue(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string dataDir = ArgValue("--selftest-data") ?? Path.Combine(Path.GetTempPath(), "netpulse-selftest-" + Guid.NewGuid().ToString("N"));
        string? outFile = ArgValue("--selftest-out");

        var report = SelfTest.Run(dataDir);
        string json = SelfTest.ToJson(report);
        if (outFile != null) File.WriteAllText(outFile, json);
        return report.Ok ? 0 : 1;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        base.OnExit(e);
    }
}
