using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NetPulse.Core;

namespace NetPulse.App;

/// <summary>UTC timestamp -> local HH:mm:ss (people read clocks in their own time zone).</summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime t ? (t.Kind == DateTimeKind.Local ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime()).ToString("HH:mm:ss") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Internal event type ("LatencySpike") -> human label ("Latency spike").</summary>
public sealed class EventLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Lexicon.EventLabel(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Severity string -> label.</summary>
public sealed class SeverityLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Lexicon.SeverityLabel(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Severity string -> brush, so warnings and problems are visible at a glance.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    // read from the active theme at conversion time (a converter cannot hold a DynamicResource)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Critical" => NetPulse.App.Themes.ThemeManager.Brush("AccentRed"),
        "Warning" => NetPulse.App.Themes.ThemeManager.Brush("AccentAmber"),
        _ => NetPulse.App.Themes.ThemeManager.Brush("TextSecondary")
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound string equals the converter parameter (used to keep the sidebar selection in sync with the current tab).</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Visible when the bound count is zero (empty-state placeholders).</summary>
/// <summary>true -> Collapsed, false -> Visible (placeholders and "nothing yet" hints).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
