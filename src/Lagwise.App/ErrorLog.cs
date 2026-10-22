using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Lagwise.App;

/// <summary>
/// Writes unexpected errors to a local log (%LOCALAPPDATA%\Lagwise\logs\errors.log) so they can be reported and fixed.
/// Nothing is sent anywhere. The user is told once per kind of error, not on every repeat.
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Shown = new();
    private const long MaxBytes = 1_000_000;

    public static string? Directory { get; set; }
    public static string? FilePath => Directory == null ? null : Path.Combine(Directory, "errors.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            if (FilePath == null) return;
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Lagwise {Core.AppInfo.Version} - {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // logging must never cause a second failure
        }
    }

    public static void TellUserOnce(Exception ex)
    {
        string key = ex.GetType().FullName + "|" + ex.TargetSite?.Name;
        lock (Gate)
        {
            if (!Shown.Add(key)) return;
        }
        MessageBox.Show(
            $"Something went wrong, but Lagwise kept running and any recording continues.\n\n{ex.Message}\n\nDetails were saved to {FilePath}",
            "Lagwise", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
