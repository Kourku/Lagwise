using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace NetPulse.Core;

/// <summary>
/// Windows tools NetPulse starts, always by their full path in the Windows folder. Started by bare name ("netsh"),
/// Windows would look in NetPulse's own folder first, so a program of that name placed next to a portable NetPulse
/// (in Downloads, say) would run instead.
/// </summary>
public static class SystemTools
{
    public static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");
    public static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Runs a tool without a window and returns what it printed, or null when it could not run or took longer than
    /// <paramref name="timeout"/> (it is then stopped, so a hung tool never blocks NetPulse).
    /// </summary>
    public static string? Capture(string fullPath, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fullPath, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds) || !output.Wait(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True for an absolute http(s) address: the only kind of link NetPulse hands to the browser.</summary>
    public static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(u.Host);

    /// <summary>
    /// True when <paramref name="path"/> can be put between double quotes on a command line without changing its
    /// meaning: no quote and none of the characters cmd.exe treats specially.
    /// </summary>
    public static bool IsSafeCommandLinePath(string path) =>
        path.Length > 0 && path.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n' }) < 0 && Path.IsPathFullyQualified(path);
}
