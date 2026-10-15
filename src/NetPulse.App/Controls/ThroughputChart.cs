using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NetPulse.Core.Services;

namespace NetPulse.App.Controls;

/// <summary>A point of the ping line drawn over the traffic (typical reference ping in that second).</summary>
public sealed record PingPoint(DateTime T, double Ms);

/// <summary>
/// Download / upload chart for the Network activity page and for recordings in History: two gradient-filled areas
/// over a time window, a "nice" auto-scaled speed axis and a hover read-out. Optional extras:
/// <list type="bullet">
/// <item><see cref="Mirrored"/>: download above the middle line, upload below, so the two never hide each other;</item>
/// <item><see cref="PingPoints"/>: the reference ping as a line on its own axis on the right, which shows bufferbloat as it
/// happens (ping climbing while a download runs);</item>
/// <item><see cref="Markers"/>: amber dashes, for moments marked as laggy in a recording.</item>
/// </list>
/// Colours are read from the active theme at render time.
/// </summary>
public class ThroughputChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = Reg(nameof(Points), typeof(IReadOnlyList<ActivityPoint>), null);
    public static readonly DependencyProperty WindowSecondsProperty = Reg(nameof(WindowSeconds), typeof(double), 60.0);
    public static readonly DependencyProperty MirroredProperty = Reg(nameof(Mirrored), typeof(bool), false);
    public static readonly DependencyProperty PingPointsProperty = Reg(nameof(PingPoints), typeof(IReadOnlyList<PingPoint>), null);
    public static readonly DependencyProperty MarkersProperty = Reg(nameof(Markers), typeof(IReadOnlyList<DateTime>), null);

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(ThroughputChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ActivityPoint>? Points { get => (IReadOnlyList<ActivityPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public bool Mirrored { get => (bool)GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }
    public IReadOnlyList<PingPoint>? PingPoints { get => (IReadOnlyList<PingPoint>?)GetValue(PingPointsProperty); set => SetValue(PingPointsProperty, value); }
    public IReadOnlyList<DateTime>? Markers { get => (IReadOnlyList<DateTime>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    private static Brush Theme(string key) => NetPulse.App.Themes.ThemeManager.Brush(key);
    private static Color ColorOf(string key) => Theme(key) is SolidColorBrush b ? b.Color : Colors.Gray;
    private static readonly Color PingColor = Color.FromRgb(244, 114, 182);
    private static readonly Pen MarkerPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(251, 191, 36)), 1.4) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });

    private double? _hoverX;
    private const double Left = 66, Top = 10, Bottom = 24;
    private double RightMargin => PingPoints is { Count: > 0 } ? 54 : 12;

    public ThroughputChart()
    {
        Loaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed -= OnThemeChanged;
    }

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
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

    /// <summary>Rounds a maximum up to 1, 2, 2.5 or 5 times a power of ten, so axes read 0 / 2.5 / 5 / 7.5 / 10 rather than odd numbers.</summary>
    public static double NiceCeiling(double value, double floor = 1_000_000)
    {
        if (value <= 0) return floor;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (value <= m * exp) return Math.Max(floor, m * exp);
        return Math.Max(floor, 10 * exp);
    }

    /// <summary>Ping axis top: a multiple of 20 ms (100 ms above 200), so the quarter lines read 5, 10, 15 ... ms.</summary>
    public static double PingCeiling(double ms) => ms <= 20 ? 20 : ms <= 200 ? Math.Ceiling(ms / 20) * 20 : Math.Ceiling(ms / 100) * 100;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double right = RightMargin;
        if (w < Left + right + 20 || h < Top + Bottom + 20) return;
        var plot = new Rect(Left, Top, w - Left - right, h - Top - Bottom);
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for hover

        var all = Points ?? Array.Empty<ActivityPoint>();
        DateTime end = all.Count > 0 ? all[^1].T : DateTime.UtcNow;
        DateTime start = end.AddSeconds(-WindowSeconds);
        var pts = all.Where(p => p.T >= start).ToList();
        var pings = (PingPoints ?? Array.Empty<PingPoint>()).Where(p => p.T >= start && p.T <= end).ToList();
        bool mirrored = Mirrored;

        // mirrored: both halves share one scale, so a download and an upload of the same speed are the same height
        double peak = mirrored ? pts.Select(p => Math.Max(p.RxBps, p.TxBps)).DefaultIfEmpty(0).Max()
                               : pts.Select(p => Math.Max(p.RxBps, p.TxBps)).DefaultIfEmpty(0).Max();
        double max = NiceCeiling(peak * 1.08);
        double pingMax = PingCeiling(pings.Select(p => p.Ms).DefaultIfEmpty(0).Max() * 1.15);
        double zero = mirrored ? plot.Top + plot.Height / 2 : plot.Bottom;
        double half = mirrored ? plot.Height / 2 : plot.Height;

        var grid = Frozen(new Pen(Theme("Border"), 1));
        var label = Theme("TextSecondary");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // speed axis
        int bands = mirrored ? 2 : 4;
        for (int i = -(mirrored ? bands : 0); i <= bands; i++)
        {
            double y = zero - half * i / bands;
            dc.DrawLine(i == 0 && mirrored ? Frozen(new Pen(Theme("BorderStrong"), 1)) : grid, new Point(plot.Left, Math.Round(y) + 0.5), new Point(plot.Right, Math.Round(y) + 0.5));
            string text = i == 0 ? "0" : AdapterActivityMonitor.FormatBps(max * Math.Abs(i) / bands);
            if (mirrored && i != 0) text = (i > 0 ? "↓ " : "↑ ") + text;
            var t = Text(text, 11, label, dpi);
            dc.DrawText(t, new Point(plot.Left - 8 - t.Width, y - t.Height / 2));
            // ping axis on the right
            if (pings.Count > 0 && (!mirrored || i >= 0))
            {
                double frac = mirrored ? (double)i / bands : (double)i / bands;
                var pt = Text($"{pingMax * frac:0} ms", 11, new SolidColorBrush(PingColor), dpi);
                dc.DrawText(pt, new Point(plot.Right + 8, y - pt.Height / 2));
            }
        }

        // time axis
        int ticks = 4;
        for (int i = 0; i <= ticks; i++)
        {
            double x = plot.Left + plot.Width * i / ticks;
            double secondsAgo = WindowSeconds * (ticks - i) / ticks;
            string s = i == ticks ? (WindowSeconds > 3600 ? end.ToLocalTime().ToString("HH:mm") : "now")
                : WindowSeconds > 3600 ? start.AddSeconds(WindowSeconds * i / ticks).ToLocalTime().ToString("HH:mm")
                : secondsAgo >= 120 ? $"-{secondsAgo / 60:0} min" : $"-{secondsAgo:0} s";
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
        double Up(double v) => zero - half * Math.Min(1, v / max);
        double Down(double v) => zero + half * Math.Min(1, v / max);
        double PingY(double ms) => (mirrored ? zero : plot.Bottom) - (mirrored ? half : plot.Height) * Math.Min(1, ms / pingMax);

        dc.PushClip(new RectangleGeometry(plot));
        if (mirrored)
        {
            DrawSeries(dc, pts, p => p.RxBps, X, Up, zero, ColorOf("AccentBlue"), downward: false);
            DrawSeries(dc, pts, p => p.TxBps, X, Down, zero, ColorOf("AccentAmber"), downward: true);
        }
        else
        {
            // upload first, so the (usually larger) download area is drawn over it
            DrawSeries(dc, pts, p => p.TxBps, X, Up, zero, ColorOf("AccentAmber"), downward: false);
            DrawSeries(dc, pts, p => p.RxBps, X, Up, zero, ColorOf("AccentBlue"), downward: false);
        }

        if (pings.Count >= 2)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(pings[0].T), PingY(pings[0].Ms)), false, false);
                c.PolyLineTo(pings.Skip(1).Select(p => new Point(X(p.T), PingY(p.Ms))).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, Frozen(new Pen(new SolidColorBrush(PingColor), 1.6) { LineJoin = PenLineJoin.Round }), g);
        }

        foreach (var m in Markers ?? Array.Empty<DateTime>())
        {
            if (m < start || m > end) continue;
            double mx = X(m);
            dc.DrawLine(MarkerPen, new Point(mx, plot.Top), new Point(mx, plot.Bottom));
        }
        dc.Pop();

        if (_hoverX is double hx && hx >= plot.Left && hx <= plot.Right)
        {
            var nearest = pts.OrderBy(p => Math.Abs(X(p.T) - hx)).First();
            double cx = X(nearest.T);
            dc.DrawLine(Frozen(new Pen(Theme("BorderStrong"), 1)), new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            dc.DrawEllipse(Theme("AccentBlue"), null, new Point(cx, Up(nearest.RxBps)), 4, 4);
            dc.DrawEllipse(Theme("AccentAmber"), null, new Point(cx, mirrored ? Down(nearest.TxBps) : Up(nearest.TxBps)), 4, 4);

            var lines = new List<string>
            {
                nearest.T.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                $"↓ {AdapterActivityMonitor.FormatBps(nearest.RxBps)}   {nearest.RxPps:0} packets/s",
                $"↑ {AdapterActivityMonitor.FormatBps(nearest.TxBps)}   {nearest.TxPps:0} packets/s"
            };
            var ping = pings.Count > 0 ? pings.OrderBy(p => Math.Abs((p.T - nearest.T).TotalSeconds)).First() : null;
            if (ping != null && Math.Abs((ping.T - nearest.T).TotalSeconds) <= 3) lines.Add($"Ping {ping.Ms:0} ms");
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
        Func<DateTime, double> x, Func<double, double> y, double baseline, Color color, bool downward)
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

        // the fill fades towards the baseline in both orientations
        var strong = Color.FromArgb(110, color.R, color.G, color.B);
        var faint = Color.FromArgb(8, color.R, color.G, color.B);
        var fill = new LinearGradientBrush(downward ? faint : strong, downward ? strong : faint, 90);
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
}
