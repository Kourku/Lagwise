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
    private static readonly Brush Critical = Frozen(239, 68, 68);
    private static readonly Brush Warning = Frozen(245, 158, 11);
    private static readonly Brush Info = Frozen(156, 163, 175);

    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Critical" => Critical,
        "Warning" => Warning,
        _ => Info
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
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
