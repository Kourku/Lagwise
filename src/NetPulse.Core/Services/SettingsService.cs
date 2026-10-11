using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetPulse.Core.Models;

namespace NetPulse.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Saves are atomic (temp file + replace);
/// a corrupt file is preserved as *.corrupt and defaults are used instead of crashing.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();

    public string FilePath { get; }
    public AppSettings Current { get; private set; } = AppSettings.CreateDefault();
    /// <summary>Set when Load could not use the existing file (corrupt or unreadable); null otherwise.</summary>
    public string? LastLoadWarning { get; private set; }

    public SettingsService(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Environment variable that redirects all user data (settings, sessions); used for isolated test runs.</summary>
    public const string DataDirectoryVariable = "NETPULSE_DATA_DIR";

    /// <summary>User data always lives under %LOCALAPPDATA%\NetPulse, never beside the executable.</summary>
    public static string DefaultDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            LastLoadWarning = null;
            var loaded = AppSettings.CreateDefault();

            if (File.Exists(FilePath))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                    if (parsed != null) loaded = parsed;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    LastLoadWarning = $"Settings file could not be read ({ex.Message}); defaults were used.";
                    TryPreserveCorruptFile();
                }
            }

            loaded.Normalize();
            Current = loaded;
            return loaded;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            // The monitoring engine rewrites the gateway target with the discovered address; persist the
            // "auto" placeholder so a stale IP from another network is never restored.
            var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, JsonOptions), JsonOptions)!;
            foreach (var gw in snapshot.Targets.Where(t => t.IsGateway))
            {
                gw.Host = "auto";
                gw.Name = "Local Gateway / Router";
            }

            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>Saves, returning false (and the reason) instead of throwing.</summary>
    public bool TrySave(out string? error)
    {
        try
        {
            Save();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort only
        }
    }
}
