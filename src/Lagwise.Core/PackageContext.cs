using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lagwise.Core;

/// <summary>
/// Whether this Lagwise runs from an installed Windows app package (the Microsoft Store edition) rather than from a folder.
/// A packaged app has its own rules: it is started through an alias instead of by file path, a "run at sign-in" entry is a
/// manifest startup task instead of a Run key, and the Windows Store, not Lagwise, installs and updates it.
/// </summary>
public static class PackageContext
{
    private const int AppModelErrorNoPackage = 15700;
    private const string AliasName = "Lagwise.exe";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    private static bool? _isPackaged;

    /// <summary>True when the process has package identity.</summary>
    public static bool IsPackaged => _isPackaged ??= Detect();

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The program to start for this Lagwise from a jump list or a shortcut. A packaged app's files cannot be started by path
    /// from outside the package, so its alias (declared in the manifest) is used; otherwise the running program.
    /// </summary>
    public static string? LaunchPath(string? runningProgram = null, bool? packaged = null, string? aliasFolder = null)
    {
        runningProgram ??= Environment.ProcessPath;
        if (!(packaged ?? IsPackaged)) return runningProgram;
        string folder = aliasFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        string alias = Path.Combine(folder, AliasName);
        return File.Exists(alias) ? alias : runningProgram;
    }
}
