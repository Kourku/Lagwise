using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NetPulse.Core.Services;

namespace NetPulse.App.Controls;

/// <summary>
/// Live download / upload chart for the Network activity page: two gradient-filled areas over a rolling time window,
/// a "nice" auto-scaled speed axis, and a hover read-out. Download uses the accent colour, upload amber, both read
/// from the active theme at render time.
/// </summary>
public class ThroughputChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ActivityPoint>), typeof(ThroughputChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(ThroughputChart), new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ActivityPoint>? Points { get => (IReadOnlyList<ActivityPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    private static Brush Theme(string key) => NetPulse.App.Themes.ThemeManager.Brush(key);
    private static Color ColorOf(string key) => Theme(key) is SolidColorBrush b ? b.Color : Colors.Gray;

    private double? _hoverX;
    private const double Left = 64, Right = 12, Top = 10, Bottom = 24;

    public ThroughputChart()
    {
        Loaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public Peer(ThroughputChart owner) : base(owner) { }
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(ThroughputChart);
        protected override string GetHelpTextCore()
        {
            var last = ((ThroughputChart)Owner).Points?.LastOrDefault();
            return last == null ? "No data yet" : $"Download {AdapterActivityMonitor.FormatBps(last.RxBps)}, upload {AdapterActivityMonitor.FormatBps(last.TxBps)}";
        }
    }

    /// <summary>Rounds a maximum up to 1, 2 or 5 times a power of ten, so the axis reads 0 / 2.5 / 5 / 7.5 / 10 Mbps rather than odd numbers.</summary>
    public static double NiceCeiling(double value)
    {
        if (value <= 0) return 1_000_000;   // an idle adapter still gets a 1 Mbps axis
        double exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (value <= m * exp) return m * exp;
        return 10 * exp;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < Left + Right + 20 || h < Top + Bottom + 20) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for hover

        var all = Points ?? Array.Empty<ActivityPoint>();
        DateTime end = all.Count > 0 ? all[^1].T : DateTime.UtcNow;
        DateTime start = end.AddSeconds(-WindowSeconds);
        var pts = all.Where(p => p.T >= start).ToList();

        double max = NiceCeiling(pts.Select(p => Math.Max(p.RxBps, p.TxBps)).DefaultIfEmpty(0).Max() * 1.08);
        var grid = new Pen(Theme("Border"), 1);
        grid.Freeze();
        var label = Theme("TextSecondary");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // speed axis: 4 bands
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(grid, new Point(plot.Left, Math.Round(y) + 0.5), new Point(plot.Right, Math.Round(y) + 0.5));
            var t = Text(AdapterActivityMonitor.FormatBps(max * i / 4), 11, label, dpi);
            dc.DrawText(t, new Point(plot.Left - 8 - t.Width, y - t.Height / 2));
        }

        // time axis: "-60 s" ... "now"
        int ticks = 4;
        for (int i = 0; i <= ticks; i++)
        {
            double x = plot.Left + plot.Width * i / ticks;
            double secondsAgo = WindowSeconds * (ticks - i) / ticks;
            string s = i == ticks ? "now" : secondsAgo >= 120 ? $"-{secondsAgo / 60:0} min" : $"-{secondsAgo:0} s";
            var t = Text(s, 11, label, dpi);
            dc.DrawText(t, new Point(Math.Min(plot.Right - t.Width, Math.Max(plot.Left, x - t.Width / 2)), plot.Bottom + 6));
        }

        if (pts.Count < 2)
        {
            var t = Text("Collecting the first seconds of activity...", 13, label, dpi);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }

        double X(DateTime t) => plot.Left + plot.Width * ((t - start).TotalSeconds / WindowSeconds);
        double Y(double v) => plot.Bottom - plot.Height * Math.Min(1, v / max);

        dc.PushClip(new RectangleGeometry(plot));
        // upload first, so the (usually larger) download area is drawn over it
        DrawSeries(dc, pts, p => p.TxBps, X, Y, plot.Bottom, ColorOf("AccentAmber"));
        DrawSeries(dc, pts, p => p.RxBps, X, Y, plot.Bottom, ColorOf("AccentBlue"));
        dc.Pop();

        if (_hoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            var nearest = pts.OrderBy(p => Math.Abs(X(p.T) - hx)).First();
            double cx = X(nearest.T);
            var cursor = new Pen(Theme("BorderStrong"), 1);
            dc.DrawLine(cursor, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            dc.DrawEllipse(Theme("AccentBlue"), null, new Point(cx, Y(nearest.RxBps)), 4, 4);
            dc.DrawEllipse(Theme("AccentAmber"), null, new Point(cx, Y(nearest.TxBps)), 4, 4);

            var lines = new[]
            {
                nearest.T.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                $"↓ {AdapterActivityMonitor.FormatBps(nearest.RxBps)}   {nearest.RxPps:0} packets/s",
                $"↑ {AdapterActivityMonitor.FormatBps(nearest.TxBps)}   {nearest.TxPps:0} packets/s"
            };
            var texts = lines.Select(l => Text(l, 12, Theme("TextPrimary"), dpi)).ToList();
            double tw = texts.Max(t => t.Width) + 20, th = texts.Sum(t => t.Height) + 14;
            double tx = cx + 12 + tw > plot.Right ? cx - 12 - tw : cx + 12;
            var box = new Rect(tx, plot.Top + 6, tw, th);
            dc.DrawRoundedRectangle(Theme("PopupBg"), new Pen(Theme("BorderStrong"), 1), box, 6, 6);
            double ty = box.Top + 7;
            foreach (var t in texts) { dc.DrawText(t, new Point(box.Left + 10, ty)); ty += t.Height; }
        }
    }

    private static void DrawSeries(DrawingContext dc, List<ActivityPoint> pts, Func<ActivityPoint, double> value,
        Func<DateTime, double> x, Func<double, double> y, double baseline, Color color)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(x(pts[0].T), y(value(pts[0]))), false, false);
            g.PolyLineTo(pts.Skip(1).Select(p => new Point(x(p.T), y(value(p)))).ToList(), true, true);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x(pts[0].T), baseline), true, true);
            g.PolyLineTo(pts.Select(p => new Point(x(p.T), y(value(p)))).Append(new Point(x(pts[^1].T), baseline)).ToList(), false, true);
        }
        line.Freeze();
        area.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(110, color.R, color.G, color.B), Color.FromArgb(8, color.R, color.G, color.B), 90);
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
}
