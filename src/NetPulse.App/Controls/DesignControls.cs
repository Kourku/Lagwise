using System;
using System.Linq;
using System.Windows;
using Control = System.Windows.Controls.Control;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace NetPulse.App.Controls;

/// <summary>Greyed-out example text inside an empty TextBox: <c>controls:Hint.Text="e.g. 9.9.9.9"</c>.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(""));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
}

/// <summary>
/// Text inside a control template is hidden from screen readers and UI Automation, so each building block exposes
/// itself as one text element whose name is its visible text.
/// </summary>
internal sealed class TextBlockLikePeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
{
    private readonly Func<string> _name;
    public TextBlockLikePeer(FrameworkElement owner, Func<string> name) : base(owner) { _name = name; }
    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Text;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override string GetNameCore()
    {
        var explicitName = base.GetNameCore();
        return string.IsNullOrEmpty(explicitName) ? _name() : explicitName;
    }
    protected override System.Collections.Generic.List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    internal static string Join(params string?[] parts) => string.Join(". ", Array.FindAll(parts, p => !string.IsNullOrWhiteSpace(p)));
}

// Small presentational building blocks. Their look lives in Themes/DarkTheme.xaml (implicit styles by type),
// so every page uses the same header, callout, empty state and stat tile.

/// <summary>Page title with an icon tile, a one-line subtitle and optional actions on the right.</summary>
public class PageHeader : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    // the header's actions (buttons, switches) stay reachable, so only the title text is named; children are kept
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new HeaderPeer(this);

    private sealed class HeaderPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public HeaderPeer(PageHeader owner) : base(owner) { }
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Header;
        protected override string GetClassNameCore() => nameof(PageHeader);
        protected override string GetNameCore() => ((PageHeader)Owner).Title;
        protected override string GetHelpTextCore() => ((PageHeader)Owner).Subtitle;
    }
}

public enum CalloutKind { Info, Success, Warning, Error }

/// <summary>Tinted message bar with an icon, for disclosures, warnings and results (Fluent "InfoBar").</summary>
public class Callout : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(CalloutKind), typeof(Callout), new PropertyMetadata(CalloutKind.Info));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(Callout), new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Callout), new PropertyMetadata(""));

    public CalloutKind Kind { get => (CalloutKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TextBlockLikePeer(this, () => TextBlockLikePeer.Join(Title, Text));
}

/// <summary>Centred icon, title and hint shown where a list or result is still empty.</summary>
public class EmptyState : Control
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TextBlockLikePeer(this, () => TextBlockLikePeer.Join(Title, Text));
}

/// <summary>
/// The NetPulse mark: a pulse line across a rounded square, drawn as vectors so it stays sharp from the 16-pixel tray
/// icon to the title bar. The app icon (app.ico) is rendered from <see cref="Draw"/> too.
/// </summary>
public class LogoMark : FrameworkElement
{
    private static readonly Color From = Color.FromRgb(0x63, 0x66, 0xF1), To = Color.FromRgb(0xA8, 0x55, 0xF7);

    protected override void OnRender(DrawingContext dc) => Draw(dc, Math.Min(ActualWidth, ActualHeight));

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TextBlockLikePeer(this, () => "NetPulse");

    public static void Draw(DrawingContext dc, double size)
    {
        if (size <= 0) return;
        double k = size / 64.0;
        var square = new RectangleGeometry(new Rect(0, 0, size, size), 15 * k, 15 * k);
        square.Freeze();
        var fill = new LinearGradientBrush(From, To, new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, square);

        // a soft light from the top, kept inside the rounded square
        dc.PushClip(square);
        var light = new LinearGradientBrush(Color.FromArgb(64, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 1));
        light.Freeze();
        dc.DrawRectangle(light, null, new Rect(0, 0, size, size * 0.6));
        dc.Pop();

        // the pulse: flat, a beat, flat again, ending in a dot (the probe coming back)
        double stroke = size < 24 ? 7.5 * k : 5.4 * k;
        var pen = new Pen(Brushes.White, stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var points = new[] { (9.0, 35.0), (20.0, 35.0), (25.5, 21.0), (32.5, 47.0), (38.5, 27.0), (42.5, 35.0), (47.0, 35.0) };
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(points[0].Item1 * k, points[0].Item2 * k), false, false);
            foreach (var (x, y) in points.Skip(1)) g.LineTo(new Point(x * k, y * k), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
        dc.DrawEllipse(Brushes.White, null, new Point(54.5 * k, 35 * k), (size < 24 ? 5.2 : 4.4) * k, (size < 24 ? 5.2 : 4.4) * k);
    }
}

/// <summary>
/// A round status mark that reads without colour: a tick (good), "!" (keep an eye on it), a cross (problem), a dash
/// (unknown) or dots (checking). Screen readers hear the word.
/// </summary>
public class StatusMark : Control
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(string), typeof(StatusMark), new PropertyMetadata("Unknown"));
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(StatusMark), new PropertyMetadata(9.0));

    /// <summary>Size of the symbol; raise it with the mark's size.</summary>
    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }

    /// <summary>"Good", "Fair", "Poor", "Info", "Unknown" or "Busy".</summary>
    public string Level { get => (string)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TextBlockLikePeer(this, () => Level switch
    {
        "Good" => "Good",
        "Fair" => "Worth a look",
        "Poor" => "Problem",
        "Busy" => "Checking",
        "Info" => "For your information",
        _ => "Unknown"
    });
}

/// <summary>A metric card: icon, label (with an explanation on hover), big value with unit, and a caption.</summary>
public class StatTile : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata("-"));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info), typeof(string), typeof(StatTile), new PropertyMetadata(null));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(StatTile), new PropertyMetadata(null));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(nameof(Badge), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty BadgeBrushProperty = DependencyProperty.Register(nameof(BadgeBrush), typeof(Brush), typeof(StatTile), new PropertyMetadata(null));
    public static readonly DependencyProperty BadgeLevelProperty = DependencyProperty.Register(nameof(BadgeLevel), typeof(string), typeof(StatTile), new PropertyMetadata("Unknown"));

    /// <summary>"Good", "Fair", "Poor" or "Unknown": picks the symbol and tint of the rating, so it reads without colour.</summary>
    public string BadgeLevel { get => (string)GetValue(BadgeLevelProperty); set => SetValue(BadgeLevelProperty, value); }

    /// <summary>One or two plain words rating the value ("Steady", "Higher than usual"); hidden when empty.</summary>
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public Brush? BadgeBrush { get => (Brush?)GetValue(BadgeBrushProperty); set => SetValue(BadgeBrushProperty, value); }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Info { get => (string?)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    /// <summary>Colour of the icon chip; the theme's accent when unset.</summary>
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new TextBlockLikePeer(this, () => TextBlockLikePeer.Join(Label, $"{Value} {Unit}".Trim(), Badge, Caption));
}

/// <summary>
/// Ring (or open arc) gauge drawn as a track plus a value arc with round caps. Content (the number) is placed in the
/// middle by the page. Pure rendering.
/// </summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    /// <summary>Total angle of the track in degrees: 360 for a full ring, 270 for an open speedometer arc.</summary>
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    /// <summary>Colour of the empty part of the ring; the theme's track colour when not set.</summary>
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public RingGauge()
    {
        Loaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => NetPulse.App.Themes.ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Thickness) / 2;
        double sweep = Math.Clamp(Sweep, 10, 360);
        double start = 90 + (360 - sweep) / 2; // opening at the bottom, angles measured clockwise from +X

        DrawArc(dc, center, r, start, sweep, new Pen(TrackBrush ?? NetPulse.App.Themes.ThemeManager.Brush("TrackBg"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        double fraction = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (fraction > 0.001)
            DrawArc(dc, center, r, start, sweep * fraction, new Pen(Brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg >= 359.99)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
