using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

/// <summary>One plain-ping reference server during one hour.</summary>
public sealed class DiaryServer
{
    /// <summary>"Icmp|1.1.1.1": stable when a server is renamed or re-added.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Probes counted for probe loss; probes during a drop are left out (the drop says it already).</summary>
    public int Probes { get; set; }
    public int Replies { get; set; }
    public double MedianMs { get; set; }
    /// <summary>Average change from one reply to the next (latency variation), as on the Overview.</summary>
    public double VariationMs { get; set; }
}

/// <summary>One hour of measurements (UTC hour), whatever NetPulse measured of it.</summary>
public sealed class DiaryHour
{
    public DateTime StartUtc { get; set; }
    /// <summary>How much of the hour NetPulse was measuring (sleep and closed time do not count).</summary>
    public double MonitoredSeconds { get; set; }
    public List<DiaryServer> Servers { get; set; } = new();
}

/// <summary>A stretch without internet ("Connection lost" to "Back online").</summary>
public sealed class DiaryOutage
{
    public DateTime StartUtc { get; set; }
    /// <summary>Null while it lasts.</summary>
    public DateTime? EndUtc { get; set; }
    /// <summary>NetPulse stopped before the internet came back: the end is the last moment it was measuring, so the length is a minimum.</summary>
    public bool EndedUnseen { get; set; }
    /// <summary>The router kept answering: the break was beyond the home network (modem or provider).</summary>
    public bool BeyondRouter { get; set; }
}

/// <summary>What the diary holds at one moment: closed hours, the hour in progress, and the drops.</summary>
public sealed record DiarySnapshot(IReadOnlyList<DiaryHour> Hours, IReadOnlyList<DiaryOutage> Outages, DateTime LastSeenUtc);

/// <summary>
/// The connection diary: how the connection did, hour by hour, whenever NetPulse was running, with or without a
/// recording. Per hour it keeps how long NetPulse measured and, for each plain-ping reference server, probes, replies,
/// typical ping and variation; separately, every drop of the internet. Kept in <c>diary.json</c> for
/// <see cref="Retention"/>; written every <see cref="FlushInterval"/> and when NetPulse closes.
/// </summary>
public sealed class ConnectionDiary
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);
    public const int MaxOutages = 500;

    /// <summary>A pause between two measurement cycles longer than this (sleep, a stalled PC) is not measured time.</summary>
    public static TimeSpan MaxCycleGap(int intervalMs) => TimeSpan.FromMilliseconds(Math.Max(5000, intervalMs * 3.0));

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public DateTime LastSeenUtc { get; set; }
        public List<DiaryHour> Hours { get; set; } = new();
        public List<DiaryOutage> Outages { get; set; } = new();
    }

    private sealed class Acc
    {
        public string Name = "";
        public readonly List<double> Rtts = new();
        public int Probes;
        public double Last = double.NaN;
        public double DiffSum;
        public int DiffCount;
    }

    private readonly string? _path;
    private readonly object _lock = new();
    private readonly List<DiaryHour> _hours = new();     // closed hours, plus a partial hour kept from an earlier run
    private readonly List<DiaryOutage> _outages = new();
    private readonly Dictionary<string, Acc> _acc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _hour;            // the hour being accumulated
    private double _monitored;
    private DateTime _lastCycle;
    private DateTime _lastSeen;
    private DateTime _lastFlush;

    /// <param name="path">Null keeps the diary in memory only (tests).</param>
    public ConnectionDiary(string? path = null)
    {
        _path = path;
        Load();
    }

    public static string KeyOf(Models.NetworkProtocol protocol, string host) => $"{protocol}|{host}";

    /// <summary>One probe to a plain-ping reference server.</summary>
    public void AddProbe(DateTime t, string key, string name, bool replied, double rttMs)
    {
        lock (_lock)
        {
            Roll(t);
            if (OpenOutage() != null) return;   // the drop itself is the news; its failed probes are not "probe loss"
            if (!_acc.TryGetValue(key, out var a)) _acc[key] = a = new Acc();
            a.Name = name;
            a.Probes++;
            if (!replied) return;
            a.Rtts.Add(rttMs);
            if (!double.IsNaN(a.Last)) { a.DiffSum += Math.Abs(rttMs - a.Last); a.DiffCount++; }
            a.Last = rttMs;
        }
    }

    /// <summary>A measurement cycle finished: the time since the previous one was measured, unless it was a long pause.</summary>
    public void AddCycle(DateTime t, int intervalMs)
    {
        bool flush;
        lock (_lock)
        {
            Roll(t);
            if (_lastCycle != default && t > _lastCycle && t - _lastCycle <= MaxCycleGap(intervalMs))
            {
                var from = _lastCycle;
                if (from < _hour)
                {
                    // the part before this hour belongs to the hour just closed
                    var previous = _hours.LastOrDefault(h => h.StartUtc == _hour.AddHours(-1));
                    if (previous != null) previous.MonitoredSeconds = Math.Min(3600, previous.MonitoredSeconds + (_hour - from).TotalSeconds);
                    from = _hour;
                }
                _monitored = Math.Min(3600, _monitored + (t - from).TotalSeconds);
            }
            if (t > _lastCycle) _lastCycle = t;
            if (t > _lastSeen) _lastSeen = t;
            flush = t - _lastFlush >= FlushInterval;
            if (flush) _lastFlush = t;
        }
        if (flush) Save();
    }

    /// <summary>The internet stopped answering ("Connection lost"), timed at the first failed cycle.</summary>
    public void OutageStarted(DateTime startUtc, bool beyondRouter)
    {
        lock (_lock)
        {
            if (OpenOutage() != null) return;
            _outages.Add(new DiaryOutage { StartUtc = startUtc, BeyondRouter = beyondRouter });
            if (_outages.Count > MaxOutages) _outages.RemoveRange(0, _outages.Count - MaxOutages);
        }
    }

    public void OutageEnded(DateTime endUtc)
    {
        lock (_lock)
        {
            var open = OpenOutage();
            if (open != null) open.EndUtc = endUtc < open.StartUtc ? open.StartUtc : endUtc;
        }
    }

    public DiarySnapshot Snapshot()
    {
        lock (_lock)
        {
            var hours = _hours.Select(Copy).ToList();
            if (_hour != default && (_monitored > 0 || _acc.Count > 0))
            {
                var current = Current();
                int i = hours.FindIndex(h => h.StartUtc == current.StartUtc);
                if (i >= 0) hours[i] = Merge(hours[i], current); else hours.Add(current);
            }
            var outages = _outages.Select(o => new DiaryOutage { StartUtc = o.StartUtc, EndUtc = o.EndUtc, EndedUnseen = o.EndedUnseen, BeyondRouter = o.BeyondRouter }).ToList();
            return new DiarySnapshot(hours.OrderBy(h => h.StartUtc).ToList(), outages, _lastSeen);
        }
    }

    /// <summary>Forgets everything (the hour in progress starts again from now).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _hours.Clear();
            _outages.Clear();
            _acc.Clear();
            _monitored = 0;
        }
        Save();
    }

    /// <summary>Writes the diary, including the hour in progress. Returns the reason when it could not be written.</summary>
    public string? Save()
    {
        if (_path == null) return null;
        FileModel model;
        lock (_lock)
        {
            var s = Snapshot();
            model = new FileModel { LastSeenUtc = s.LastSeenUtc, Hours = s.Hours.ToList(), Outages = s.Outages.ToList() };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(model));
            File.Move(_path + ".tmp", _path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private DiaryOutage? OpenOutage() => _outages.Count > 0 && _outages[^1].EndUtc == null ? _outages[^1] : null;

    private static DateTime HourOf(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    /// <summary>Moves on to the hour of <paramref name="t"/>, closing the hour in progress. Readings stamped a moment before it stay in it.</summary>
    private void Roll(DateTime t)
    {
        var hour = HourOf(t);
        if (_hour == default) { _hour = hour; return; }
        if (hour <= _hour) return;

        var closed = Current();
        if (closed.MonitoredSeconds > 0 || closed.Servers.Count > 0)
        {
            int i = _hours.FindIndex(h => h.StartUtc == closed.StartUtc);
            if (i >= 0) _hours[i] = Merge(_hours[i], closed); else _hours.Add(closed);
        }
        _acc.Clear();
        _monitored = 0;
        _hour = hour;

        var cutoff = hour - Retention;
        _hours.RemoveAll(h => h.StartUtc < cutoff);
        _outages.RemoveAll(o => (o.EndUtc ?? hour) < cutoff);
    }

    private DiaryHour Current() => new()
    {
        StartUtc = _hour,
        MonitoredSeconds = Math.Round(_monitored, 1),
        Servers = _acc.Where(kv => kv.Value.Probes > 0).Select(kv => new DiaryServer
        {
            Key = kv.Key,
            Name = kv.Value.Name,
            Probes = kv.Value.Probes,
            Replies = kv.Value.Rtts.Count,
            MedianMs = kv.Value.Rtts.Count > 0 ? Math.Round(StatisticalCalculator.CalculateMedian(kv.Value.Rtts), 2) : 0,
            VariationMs = kv.Value.DiffCount > 0 ? Math.Round(kv.Value.DiffSum / kv.Value.DiffCount, 2) : 0
        }).ToList()
    };

    /// <summary>Two parts of the same hour (NetPulse restarted within it): counts add up, typical values are averaged by replies.</summary>
    public static DiaryHour Merge(DiaryHour a, DiaryHour b)
    {
        var servers = a.Servers.Concat(b.Servers).GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var list = g.ToList();
            int replies = list.Sum(s => s.Replies);
            return new DiaryServer
            {
                Key = g.Key,
                Name = list[^1].Name,
                Probes = list.Sum(s => s.Probes),
                Replies = replies,
                MedianMs = replies > 0 ? Math.Round(list.Sum(s => s.MedianMs * s.Replies) / replies, 2) : 0,
                VariationMs = replies > 0 ? Math.Round(list.Sum(s => s.VariationMs * s.Replies) / replies, 2) : 0
            };
        }).ToList();
        return new DiaryHour { StartUtc = a.StartUtc, MonitoredSeconds = Math.Min(3600, a.MonitoredSeconds + b.MonitoredSeconds), Servers = servers };
    }

    private static DiaryHour Copy(DiaryHour h) => new()
    {
        StartUtc = h.StartUtc,
        MonitoredSeconds = h.MonitoredSeconds,
        Servers = h.Servers.Select(s => new DiaryServer { Key = s.Key, Name = s.Name, Probes = s.Probes, Replies = s.Replies, MedianMs = s.MedianMs, VariationMs = s.VariationMs }).ToList()
    };

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
            if (model == null) return;
            foreach (var h in model.Hours) h.StartUtc = DateTime.SpecifyKind(h.StartUtc, DateTimeKind.Utc);
            _hours.AddRange(model.Hours.OrderBy(h => h.StartUtc));
            foreach (var o in model.Outages)
            {
                o.StartUtc = DateTime.SpecifyKind(o.StartUtc, DateTimeKind.Utc);
                if (o.EndUtc is DateTime e) o.EndUtc = DateTime.SpecifyKind(e, DateTimeKind.Utc);
            }
            _outages.AddRange(model.Outages.OrderBy(o => o.StartUtc));
            _lastSeen = model.LastSeenUtc;
            // a drop still open when NetPulse closed: it lasted at least until the last moment NetPulse measured
            foreach (var o in _outages.Where(o => o.EndUtc == null))
            {
                o.EndUtc = _lastSeen > o.StartUtc ? _lastSeen : o.StartUtc;
                o.EndedUnseen = true;
            }
        }
        catch (JsonException)
        {
            // unreadable: keep it aside rather than overwrite it at the next save
            try { File.Move(_path, _path + ".unreadable", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // starts empty for this run
        }
    }
}
