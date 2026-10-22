using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Lagwise.App.Themes;

public sealed record AccentChoice(string Name, string DarkHex, string LightHex);

/// <summary>
/// Builds the colour palette (every "...Color" and "...Brush" key the UI uses) for the chosen theme and accent and swaps
/// it into the application resources at runtime. All XAML refers to palette brushes with DynamicResource, so a switch
/// repaints the whole UI immediately. Control styles live in Styles.xaml and never contain colours of their own.
/// </summary>
public static class ThemeManager
{
    /// <summary>Raised after a new palette is in place (custom-drawn controls repaint on it).</summary>
    public static event EventHandler? Changed;

    public static bool IsLight { get; private set; }
    public static string CurrentTheme { get; private set; } = "Dark";
    public static string CurrentAccent { get; private set; } = "Indigo";

    public static IReadOnlyList<AccentChoice> Accents { get; } = new[]
    {
        new AccentChoice("Indigo", "#5B7CFA", "#4F6BED"),
        new AccentChoice("Teal", "#14B8A6", "#0D9488"),
        new AccentChoice("Violet", "#8B5CF6", "#7C3AED"),
        new AccentChoice("Orange", "#F97316", "#EA580C"),
        new AccentChoice("Rose", "#F43F5E", "#E11D48"),
    };

    // ---- base palettes (everything except the accent family) ----

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["BgDark"] = "#0A0D13", ["BgSidebar"] = "#0D1118", ["BgCard"] = "#121721", ["BgCardHover"] = "#1A2130", ["BgInput"] = "#171E2B",
        ["Border"] = "#222A39", ["BorderStrong"] = "#2E384B",
        ["TextPrimary"] = "#EDF0F6", ["TextSecondary"] = "#98A2B5", ["TextTertiary"] = "#667085",
        ["AccentGreen"] = "#34D399", ["AccentAmber"] = "#FBBF24", ["AccentRed"] = "#F87171",
        ["GreenSoft"] = "#10261F", ["GreenEdge"] = "#1D4A3A", ["AmberSoft"] = "#2A2312", ["AmberEdge"] = "#4D3E17", ["RedSoft"] = "#2C1619", ["RedEdge"] = "#562229",
        ["HeaderBg"] = "#0F141D", ["RowHover"] = "#161D2A", ["PopupBg"] = "#141A26", ["TooltipBg"] = "#1A2130", ["ItemHighlight"] = "#1C2433",
        ["TrackBg"] = "#1E2636", ["SwitchTrack"] = "#2A3447", ["Knob"] = "#C9D1E0", ["Thumb"] = "#2E384B", ["ThumbHover"] = "#46536B",
        ["Scrim"] = "#33000000",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["BgDark"] = "#F2F4F8", ["BgSidebar"] = "#FAFBFD", ["BgCard"] = "#FFFFFF", ["BgCardHover"] = "#EEF1F6", ["BgInput"] = "#F3F5F9",
        ["Border"] = "#E0E4EB", ["BorderStrong"] = "#C9D0DB",
        ["TextPrimary"] = "#111827", ["TextSecondary"] = "#4B5565", ["TextTertiary"] = "#8590A2",
        ["AccentGreen"] = "#059669", ["AccentAmber"] = "#B86E00", ["AccentRed"] = "#DC2626",
        ["GreenSoft"] = "#E9F7F1", ["GreenEdge"] = "#B6E3D0", ["AmberSoft"] = "#FDF5E6", ["AmberEdge"] = "#F0D49C", ["RedSoft"] = "#FDEEEE", ["RedEdge"] = "#F3C4C4",
        ["HeaderBg"] = "#F7F8FB", ["RowHover"] = "#F3F6FA", ["PopupBg"] = "#FFFFFF", ["TooltipBg"] = "#FFFFFF", ["ItemHighlight"] = "#EEF2F8",
        ["TrackBg"] = "#E3E8F0", ["SwitchTrack"] = "#C9D1DD", ["Knob"] = "#FFFFFF", ["Thumb"] = "#C5CCD8", ["ThumbHover"] = "#A7B0BF",
        ["Scrim"] = "#0A000000",
    };

    /// <summary>Reads the Windows "app mode" setting (Settings > Personalisation > Colours).</summary>
    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool _listening;

    /// <summary>Applies a theme ("Dark", "Light", "System") and accent. Safe to call repeatedly.</summary>
    public static void Apply(string theme, string accent)
    {
        CurrentTheme = theme is "Light" or "System" ? theme : "Dark";
        var choice = Accents.FirstOrDefault(a => string.Equals(a.Name, accent, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];
        CurrentAccent = choice.Name;
        IsLight = CurrentTheme == "Light" || (CurrentTheme == "System" && WindowsUsesLightTheme());

        var palette = new ResourceDictionary();
        foreach (var (key, hex) in IsLight ? Light : Dark) Add(palette, key, Parse(hex));

        // accent family, derived from one colour so every accent looks consistent
        var accentColor = Parse(IsLight ? choice.LightHex : choice.DarkHex);
        var bg = Parse((IsLight ? Light : Dark)["BgCard"]);
        Add(palette, "AccentBlue", accentColor);
        Add(palette, "AccentBlueHover", IsLight ? Mix(accentColor, Colors.Black, 0.15) : Mix(accentColor, Colors.White, 0.18));
        Add(palette, "AccentSoft", Mix(bg, accentColor, IsLight ? 0.14 : 0.22));
        Add(palette, "BlueSoft", Mix(bg, accentColor, IsLight ? 0.07 : 0.12));
        Add(palette, "BlueEdge", Mix(bg, accentColor, IsLight ? 0.30 : 0.40));

        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Contains("LagwisePalette"));
        palette["LagwisePalette"] = true;
        if (old != null) merged[merged.IndexOf(old)] = palette;
        else merged.Insert(0, palette);

        if (!_listening)
        {
            _listening = true;
            // follow Windows when the user switches its app theme while Lagwise runs
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && CurrentTheme == "System")
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply("System", CurrentAccent)));
            };
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>A palette brush by key (for code that draws by hand), with a neutral fallback.</summary>
    public static Brush Brush(string key) => Application.Current?.TryFindResource(key + "Brush") as Brush ?? System.Windows.Media.Brushes.Gray;

    public static Color AccentPreview(AccentChoice a) => Parse(IsLight ? a.LightHex : a.DarkHex);

    private static void Add(ResourceDictionary d, string key, Color c)
    {
        d[key + "Color"] = c;
        var b = new SolidColorBrush(c);
        b.Freeze();
        d[key + "Brush"] = b;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
