using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lagwise.Core.Sessions;

/// <summary>
/// Local-only persistence for monitoring sessions (one JSON file per session). Nothing is transmitted.
/// Listing uses a small summary index (index.json) so the History screen does not have to parse every recording;
/// an index entry is trusted only while the file's size and write time are unchanged, so edits, deletions and files
/// copied in by hand are always picked up.
/// </summary>
public class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string IndexFileName = "index.json";
    private readonly object _indexLock = new();

    public string Directory { get; }

    public SessionStore(string directory)
    {
        Directory = directory;
    }

    private string PathFor(string id)
    {
        // ids are generated GUIDs; reject anything that could escape the directory
        if (string.IsNullOrEmpty(id) || id.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Invalid session id.", nameof(id));
        return Path.Combine(Directory, id + ".session.json");
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

        lock (_indexLock)
        {
            var index = ReadIndex();
            var fresh = new Dictionary<string, IndexEntry>();
            bool changed = false;

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.session.json"))
            {
                try
                {
                    var info = new FileInfo(file);
                    string key = info.Name;
                    long ticks = info.LastWriteTimeUtc.Ticks;

                    if (index.TryGetValue(key, out var entry) && entry.Length == info.Length && entry.WriteTicks == ticks && entry.Version == IndexVersion)
                    {
                        fresh[key] = entry;
                        result.Add(entry.Summary);
                        continue;
                    }

                    var s = JsonSerializer.Deserialize<MonitoringSession>(File.ReadAllText(file), JsonOptions);
                    changed = true;
                    if (s == null) continue;
                    var summary = SessionSummary.From(s, info.Length);
                    fresh[key] = new IndexEntry { Length = info.Length, WriteTicks = ticks, Summary = summary, Version = IndexVersion };
                    result.Add(summary);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // skip unreadable session file
                }
            }

            if (changed || fresh.Count != index.Count) WriteIndex(fresh);
        }

        return result.OrderByDescending(r => r.StartedUtc).ToList();
    }

    /// <summary>
    /// Deletes complete recordings that ended before <paramref name="cutoffUtc"/>. Interrupted recordings are kept,
    /// because they may be the only trace of a crash the user wants to look at. Returns the number deleted.
    /// </summary>
    public int DeleteOlderThan(DateTime cutoffUtc)
    {
        int deleted = 0;
        foreach (var s in List())
        {
            if (!s.IsComplete || (s.EndedUtc ?? s.StartedUtc) >= cutoffUtc) continue;
            try
            {
                if (Delete(s.Id)) deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a file in use is simply kept until the next start-up
            }
        }
        return deleted;
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> automatic recordings and deletes older ones, except recordings in which
    /// lag was marked (the ones worth keeping) and interrupted ones. Returns the number deleted.
    /// </summary>
    public int PruneAutomatic(int keep, string? exceptId = null)
    {
        if (keep <= 0) return 0;
        int deleted = 0;
        foreach (var s in List().Where(s => s.Automatic && s.IsComplete && s.LagMarkerCount == 0 && s.Id != exceptId).Skip(keep))
        {
            try
            {
                if (Delete(s.Id)) deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // kept until the next save
            }
        }
        return deleted;
    }

    /// <summary>Bumped when <see cref="SessionSummary"/> gains a field, so older index entries are worked out again.</summary>
    private const int IndexVersion = 3;

    private sealed class IndexEntry
    {
        public int Version { get; set; }
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public SessionSummary Summary { get; set; } = new();
    }

    private Dictionary<string, IndexEntry> ReadIndex()
    {
        try
        {
            string path = Path.Combine(Directory, IndexFileName);
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(); // a damaged index is just rebuilt
        }
    }

    private void WriteIndex(Dictionary<string, IndexEntry> index)
    {
        try
        {
            string path = Path.Combine(Directory, IndexFileName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index, JsonOptions));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the index is only a cache
        }
    }
}
