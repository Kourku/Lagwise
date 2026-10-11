using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NetPulse.Core.Sessions;

namespace NetPulse.App.Controls;

/// <summary>
/// Time-series chart for a recorded session: one line per target, lag markers (amber dashes), monitoring gaps (grey) and outages (red).
/// Values above the axis cap are clipped to the top edge. Pure rendering; all numbers come from <see cref="ChartData"/>.
/// </summary>
public class SessionChart : FrameworkElement
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(124, 140, 255), Color.FromRgb(52, 211, 153), Color.FromRgb(251, 191, 36), Color.FromRgb(244, 114, 182),
        Color.FromRgb(56, 189, 248), Color.FromRgb(167, 139, 250), Color.FromRgb(248, 113, 113), Color.FromRgb(163, 230, 53),
        Color.FromRgb(45, 212, 191), Color.FromRgb(251, 146, 60)
    };

    public static Color ColorFor(string targetId, IReadOnlyList<string> order)
    {
        int i = order.ToList().IndexOf(targetId);
        return Palette[(i < 0 ? 0 : i) % Palette.Length];
    }

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(SessionChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetOrderProperty = DependencyProperty.Register(
        nameof(TargetOrder), typeof(IReadOnlyList<string>), typeof(SessionChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartData? Data { get => (ChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IReadOnlyList<string>? TargetOrder { get => (IReadOnlyList<string>?)GetValue(TargetOrderProperty); set => SetValue(TargetOrderProperty, value); }

    private static readonly Brush GridBrush = Frozen(Color.FromRgb(34, 42, 57));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(152, 162, 181));
    private static readonly Brush GapBrush = Frozen(Color.FromArgb(60, 152, 162, 181));
    private static readonly Brush OutageBrush = Frozen(Color.FromArgb(70, 248, 113, 113));
    private static readonly Pen LagPen = MakePen(Color.FromRgb(251, 191, 36), 1.4, new DashStyle(new double[] { 3, 3 }, 0));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen MakePen(Color c, double w, DashStyle? dash = null)
    {
        var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Round };
        if (dash != null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;

        var data = Data;
        var plot = new Rect(54, 10, Math.Max(10, w - 54 - 14), Math.Max(10, h - 10 - 28));
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable area

        if (data == null)
        {
            Text(dc, "Select a recording to see its graph.", plot.Left + 10, plot.Top + plot.Height / 2 - 8, 13);
            return;
        }

        double cap = Math.Max(1e-6, data.YCap);
        double step = NiceStep(cap / 4);
        for (double v = 0; v <= cap + 1e-9; v += step)
        {
            double y = plot.Bottom - v / cap * plot.Height;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, FormatTick(v, data.Unit), 6, y - 8, 11.5, alignRightTo: plot.Left - 8);
        }

        double dur = Math.Max(1, data.DurationSeconds);
        double X(double sec) => plot.Left + Math.Clamp(sec / dur, 0, 1) * plot.Width;

        for (int i = 0; i <= 4; i++)
        {
            double sec = dur * i / 4;
            string label = data.StartUtc.AddSeconds(sec).ToLocalTime().ToString("HH:mm:ss");
            double x = X(sec);
            Text(dc, label, i == 0 ? x : i == 4 ? x - 52 : x - 26, plot.Bottom + 7, 11.5);
        }

        foreach (var (s, e) in data.GapSeconds) dc.DrawRectangle(GapBrush, null, new Rect(X(s), plot.Top, Math.Max(2, X(e) - X(s)), plot.Height));
        foreach (var (s, e) in data.OutageSeconds) dc.DrawRectangle(OutageBrush, null, new Rect(X(s), plot.Top, Math.Max(2, X(e) - X(s)), plot.Height));

        var order = TargetOrder ?? data.Series.Select(x => x.TargetId).ToList();
        foreach (var series in data.Series)
        {
            var pen = MakePen(ColorFor(series.TargetId, order), 1.6);
            var pts = series.Points.Select(p => new Point(X(p.X), plot.Bottom - Math.Min(p.Y, cap) / cap * plot.Height)).ToList();
            if (pts.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, pts[0], 2.5, 2.5);
                continue;
            }

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                // break the line across long holes (monitoring gaps) instead of drawing a misleading straight segment
                double holeSeconds = data.BucketSeconds * 3;
                for (int i = 1; i < pts.Count; i++)
                {
                    bool hole = series.Points[i].X - series.Points[i - 1].X > holeSeconds;
                    if (hole) ctx.BeginFigure(pts[i], false, false);
                    else ctx.LineTo(pts[i], true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        foreach (var sec in data.LagMarkerSeconds)
        {
            double x = X(sec);
            dc.DrawLine(LagPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var tri = new StreamGeometry();
            using (var c = tri.Open())
            {
                c.BeginFigure(new Point(x - 5, plot.Top), true, true);
                c.LineTo(new Point(x + 5, plot.Top), true, false);
                c.LineTo(new Point(x, plot.Top + 8), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(LagPen.Brush, null, tri);
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / pow;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return nice * pow;
    }

    private static string FormatTick(double v, string unit) => unit == "%" ? $"{v:0}%" : v >= 1000 ? $"{v / 1000:0.#} s" : $"{v:0} ms";

    private void Text(DrawingContext dc, string text, double x, double y, double size, double? alignRightTo = null)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(alignRightTo.HasValue ? alignRightTo.Value - ft.Width : x, y));
    }
}
