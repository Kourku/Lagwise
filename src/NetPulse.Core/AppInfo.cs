using System.Reflection;

namespace NetPulse.Core;

public static class AppInfo
{
    /// <summary>Product version (major.minor.patch) from Directory.Build.props.</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
