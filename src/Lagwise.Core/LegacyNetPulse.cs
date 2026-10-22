using System;
using System.IO;

namespace Lagwise.Core;

/// <summary>
/// Lagwise was called NetPulse up to version 1.0. Its data lived in <c>%LOCALAPPDATA%\NetPulse</c>, its startup entry
/// was named "NetPulse" and its installer registered "NetPulse". This carries a user's data over once.
/// </summary>
public static class LegacyNetPulse
{
    public const string OldName = "NetPulse";

    public static string OldDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);

    /// <summary>
    /// Copies NetPulse's settings, recordings, diary and other data into the new folder the first time Lagwise starts
    /// (only when the new folder does not exist yet). The old folder is left as it was: an old NetPulse may still be
    /// running, and nothing is lost if the copy is interrupted. Returns true when data was copied.
    /// </summary>
    public static bool CopyDataIfFirstStart(string oldDirectory, string newDirectory)
    {
        if (Directory.Exists(newDirectory) || !Directory.Exists(oldDirectory)) return false;
        string staging = newDirectory.TrimEnd('\\', '/') + ".moving";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);   // a copy cut short last time
            CopyTree(oldDirectory, staging);
            Directory.Move(staging, newDirectory);   // all or nothing: a half copy never looks like real data
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            // NetPulse wrote every file to a temporary name first; leftovers of an interrupted write are not data
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never follow links out of the folder
            CopyTree(dir, Path.Combine(to, info.Name));
        }
    }
}
