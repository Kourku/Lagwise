using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPulse.Core.Sessions;

/// <summary>Local-only persistence for monitoring sessions (one JSON file per session). Nothing is transmitted.</summary>
public class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Directory { get; }

    public SessionStore(string directory)
    {
        Directory = directory;
    }

    private string PathFor(string id)
    {
        // ids are generated GUIDs; reject anything that could escape the directory
        if (id.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Invalid session id.", nameof(id));
        return System.IO.Path.Combine(Directory, id + ".session.json");
    }

    public void Save(MonitoringSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathFor(session.Id);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(session, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public MonitoringSession? Load(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<MonitoringSession>(File.ReadAllText(path), JsonOptions);
    }

    public bool Delete(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Newest first. Unreadable files are skipped rather than failing the whole listing.</summary>
    public IReadOnlyList<SessionSummary> List()
    {
        var result = new List<SessionSummary>();
        if (!System.IO.Directory.Exists(Directory)) return result;

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.session.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<MonitoringSession>(File.ReadAllText(file), JsonOptions);
                if (s == null) continue;
                result.Add(new SessionSummary
                {
                    Id = s.Id,
                    Name = s.Name,
                    GameId = s.GameId,
                    StartedUtc = s.StartedUtc,
                    EndedUtc = s.EndedUtc,
                    IsComplete = s.IsComplete,
                    SampleCount = s.Samples.Count,
                    EventCount = s.Events.Count
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // skip unreadable session file
            }
        }

        return result.OrderByDescending(r => r.StartedUtc).ToList();
    }
}
