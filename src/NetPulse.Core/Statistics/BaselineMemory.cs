using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NetPulse.Core.Statistics;

/// <summary>
/// Long-term memory of each target's normal latency ("usual"), kept on disk across restarts.
/// Every <see cref="SampleInterval"/> of monitoring the current typical (median) latency of each target is stored;
/// a target's usual latency is the median of its stored values. Without it, "usual" was only the last 15 minutes:
/// after a restart during a slow evening the slow ping itself became "usual for you".
/// A target needs <see cref="MinValues"/> stored values before its memory is used, and only the last
/// <see cref="MaxValues"/> values from the last <see cref="MaxAge"/> are kept, so a lasting change (new provider,
/// moving house) becomes the new normal within a few hours of monitoring.
/// </summary>
public sealed class BaselineMemory
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public const int MinValues = 3;
    public const int MaxValues = 48;
    public const int MaxTargets = 64;

    public sealed class Entry
    {
        public DateTime T { get; set; }
        public double Ms { get; set; }
    }

    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<string, List<Entry>> _values = new(StringComparer.OrdinalIgnoreCase);

    public BaselineMemory(string? path = null)
    {
        _path = path;
        Load();
    }

    public static string KeyOf(Models.NetworkProtocol protocol, string host) => $"{protocol}|{host}";

    /// <summary>The remembered usual latency, or null while fewer than <see cref="MinValues"/> values are stored.</summary>
    public double? Usual(string key)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var list) || list.Count < MinValues) return null;
            return StatisticalCalculator.CalculateMedian(list.Select(e => e.Ms).ToList());
        }
    }

    public int Count(string key)
    {
        lock (_lock) return _values.TryGetValue(key, out var list) ? list.Count : 0;
    }

    public void Add(string key, double medianMs, DateTime nowUtc)
    {
        if (!(medianMs > 0) || double.IsInfinity(medianMs)) return;
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var list))
            {
                if (_values.Count >= MaxTargets)
                {
                    // forget the target measured longest ago
                    var oldest = _values.OrderBy(kv => kv.Value.Count == 0 ? DateTime.MinValue : kv.Value[^1].T).First().Key;
                    _values.Remove(oldest);
                }
                _values[key] = list = new List<Entry>();
            }
            list.Add(new Entry { T = nowUtc, Ms = Math.Round(medianMs, 2) });
            list.RemoveAll(e => nowUtc - e.T > MaxAge);
            if (list.Count > MaxValues) list.RemoveRange(0, list.Count - MaxValues);
        }
    }

    public void Save()
    {
        if (_path == null) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_values);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", json);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory for this run
        }
    }

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(File.ReadAllText(_path));
            if (loaded != null) _values = new Dictionary<string, List<Entry>>(loaded.Where(kv => kv.Value != null), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // unreadable: start learning again
        }
    }
}
