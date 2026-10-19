using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetPulse.Core.Models;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

public enum TestKind
{
    Speed,
    Bufferbloat,
    /// <summary>Not a test NetPulse ran: ping seen while this PC itself moved a lot of data (a download, an update).</summary>
    RealLoad
}

/// <summary>One stored result. Only numbers and the kind of connection are kept: no addresses, adapter or network names.</summary>
public sealed class TestRecord
{
    public DateTime T { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TestKind Kind { get; set; }
    /// <summary>"Wired", "Wi-Fi" or empty when unknown.</summary>
    public string Connection { get; set; } = "";
    /// <summary>Speed test: the result. Bufferbloat: the speed reached under load. Real load: average speed of the heavy seconds.</summary>
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
    /// <summary>Bufferbloat: ping at rest. Real load: typical ping in the minute before the traffic.</summary>
    public double RestMs { get; set; }
    /// <summary>Bufferbloat only: how much ping rose while downloading and while uploading.</summary>
    public double DownDeltaMs { get; set; }
    public double UpDeltaMs { get; set; }
    public string? Grade { get; set; }
    /// <summary>Real load only: typical ping during the traffic, the rise (per server, then the median), probes without a reply.</summary>
    public double LoadedMs { get; set; }
    public double RiseMs { get; set; }
    public double LossPercent { get; set; }
    public double PeakDownMbps { get; set; }
    public double PeakUpMbps { get; set; }
    public int Seconds { get; set; }

    /// <summary>The ping rise this record shows: the larger bufferbloat phase, or the rise seen in real use.</summary>
    [JsonIgnore] public double WorstRiseMs => Kind == TestKind.RealLoad ? RiseMs : Math.Max(DownDeltaMs, UpDeltaMs);

    public static string ConnectionOf(NetworkInterfaceInfo? nic) =>
        nic == null || string.IsNullOrEmpty(nic.Id) ? "" : nic.IsWifi ? "Wi-Fi" : "Wired";

    public static TestRecord FromSpeed(SpeedTestResult r, string connection) => new()
    {
        T = r.Timestamp, Kind = TestKind.Speed, Connection = connection,
        DownMbps = Math.Round(r.DownloadSpeedMbps, 1), UpMbps = Math.Round(r.UploadSpeedMbps, 1)
    };

    public static TestRecord FromBufferbloat(BufferbloatResult r, string connection) => new()
    {
        T = r.Timestamp, Kind = TestKind.Bufferbloat, Connection = connection, Grade = r.Grade,
        RestMs = Math.Round(r.BaselineLatencyMs, 1), DownDeltaMs = Math.Round(r.DownloadDeltaMs, 1), UpDeltaMs = Math.Round(r.UploadDeltaMs, 1),
        DownMbps = Math.Round(r.DownloadLoadMbps, 1), UpMbps = Math.Round(r.UploadLoadMbps, 1)
    };
}

/// <summary>
/// Speed and Bufferbloat results, and ping seen under real load, kept in <c>test-history.json</c> so they can be
/// compared over days: evenings against daytime, Wi-Fi against cable. The newest <see cref="MaxRecords"/> are kept.
/// </summary>
public sealed class TestHistory
{
    public const int MaxRecords = 500;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _path;
    private readonly Func<IEnumerable<TestRecord>>? _seed;
    private readonly object _lock = new();
    private List<TestRecord>? _records;

    /// <summary>Raised after a record was added or removed (from the thread that changed it).</summary>
    public event EventHandler? Changed;

    /// <param name="path">Null keeps the history in memory only (tests).</param>
    /// <param name="seed">Records to start with when no history file exists yet (results remembered by older versions).</param>
    public TestHistory(string? path = null, Func<IEnumerable<TestRecord>>? seed = null)
    {
        _path = path;
        _seed = seed;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TestRecord> All()
    {
        lock (_lock) return Records().OrderByDescending(r => r.T).ToList();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TestRecord> Of(params TestKind[] kinds)
    {
        lock (_lock) return Records().Where(r => kinds.Contains(r.Kind)).OrderByDescending(r => r.T).ToList();
    }

    public TestRecord? Latest(TestKind kind)
    {
        lock (_lock) return Records().Where(r => r.Kind == kind).MaxBy(r => r.T);
    }

    /// <summary>Stores a record. Returns the reason when it could not be written to disk (it is still kept for this run).</summary>
    public string? Add(TestRecord record)
    {
        string? error;
        lock (_lock)
        {
            var list = Records();
            list.Add(record);
            if (list.Count > MaxRecords) list.RemoveRange(0, list.Count - MaxRecords);   // kept oldest first
            error = Save(list);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return error;
    }

    /// <summary>Forgets every record of these kinds. Returns how many were removed.</summary>
    public int Clear(params TestKind[] kinds)
    {
        int n;
        lock (_lock)
        {
            var list = Records();
            n = list.RemoveAll(r => kinds.Contains(r.Kind));
            if (n > 0) Save(list);
        }
        if (n > 0) Changed?.Invoke(this, EventArgs.Empty);
        return n;
    }

    private List<TestRecord> Records()
    {
        if (_records != null) return _records;
        _records = new List<TestRecord>();
        if (_path == null) return _records;
        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<List<TestRecord>>(File.ReadAllText(_path), Json) ?? new List<TestRecord>();
            }
            else if (_seed != null)
            {
                _records.AddRange(_seed());
                if (_records.Count > 0) Save(_records);
            }
        }
        catch (JsonException)
        {
            // unreadable: keep it aside instead of overwriting it with the next result
            try { File.Move(_path, _path + ".unreadable", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // behave as empty for this run
        }
        _records = _records.OrderBy(r => r.T).ToList();
        return _records;
    }

    private string? Save(List<TestRecord> list)
    {
        if (_path == null) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(list.OrderBy(r => r.T), Json));
            File.Move(_path + ".tmp", _path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}

/// <summary>Plain-language conclusions over stored results: typical values, evenings against daytime, Wi-Fi against cable.</summary>
public static class TestTrends
{
    /// <summary>Evenings are 18:00-23:59 local time, when home connections and providers are busiest; daytime is 06:00-17:59.</summary>
    public static bool IsEvening(DateTime local) => local.Hour >= 18;
    public static bool IsDaytime(DateTime local) => local.Hour is >= 6 and < 18;

    /// <summary>Tests needed on each side before evenings and daytime are compared.</summary>
    public const int MinPerSide = 2;

    private static Func<DateTime, DateTime> Local(Func<DateTime, DateTime>? toLocal) =>
        toLocal ?? (t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime());

    private static double Median(IEnumerable<double> v) => StatisticalCalculator.CalculateMedian(v.ToList());

    /// <param name="tests">Speed tests, any order.</param>
    public static string Speed(IReadOnlyList<TestRecord> tests, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = Local(toLocal);
        if (tests.Count == 0) return "";
        if (tests.Count == 1)
            return "One test so far. Run it again at a different time of day, for example in the evening, to see whether your speed changes when everyone is online.";

        var parts = new List<string>
        {
            $"Typical of your {tests.Count} tests: {Median(tests.Select(t => t.DownMbps)):0} Mbps down and {Median(tests.Select(t => t.UpMbps)):0.#} Mbps up " +
            $"(fastest {tests.Max(t => t.DownMbps):0}, slowest {tests.Min(t => t.DownMbps):0} Mbps down)."
        };

        var (same, only) = SameConnection(tests);
        var evening = same.Where(t => IsEvening(local(t.T))).ToList();
        var day = same.Where(t => IsDaytime(local(t.T))).ToList();
        if (evening.Count >= MinPerSide && day.Count >= MinPerSide)
        {
            double e = Median(evening.Select(t => t.DownMbps)), d = Median(day.Select(t => t.DownMbps));
            if (d > 0 && e < d * 0.85)
                parts.Add($"Evening tests are {(1 - e / d) * 100:0}% slower than daytime ones ({e:0} against {d:0} Mbps down{only}). Slower evenings usually mean your provider's network is busy at peak time.");
            else if (e > 0 && d < e * 0.85)
                parts.Add($"Daytime tests are {(1 - d / e) * 100:0}% slower than evening ones ({d:0} against {e:0} Mbps down{only}).");
            else
                parts.Add($"Evening and daytime tests are about the same{(only.Length > 0 ? $" ({only.TrimStart(',', ' ')})" : "")}, so your provider keeps up at peak time.");
        }
        else if (tests.Count >= 3)
        {
            parts.Add(evening.Count < MinPerSide
                ? "Test in the evening a couple of times too, to see whether peak time is slower."
                : "Test during the day a couple of times too, to compare with your evening results.");
        }

        parts.AddRange(ByConnection(tests, t => t.DownMbps, v => $"{v:0} Mbps down"));
        return string.Join(" ", parts);
    }

    /// <param name="tests">Bufferbloat tests, any order.</param>
    public static string Bufferbloat(IReadOnlyList<TestRecord> tests, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = Local(toLocal);
        if (tests.Count == 0) return "";
        if (tests.Count == 1)
            return "One test so far. Lag under load can change with the time of day and with what else runs at home, so test again another time.";

        var grades = tests.OrderBy(t => t.T).Select(t => t.Grade ?? "?").ToList();
        var parts = new List<string>
        {
            $"Your {tests.Count} tests: typical rise under load {Median(tests.Select(t => t.WorstRiseMs)):0} ms, largest {tests.Max(t => t.WorstRiseMs):0} ms " +
            $"(recent grades: {string.Join(", ", grades.TakeLast(6))})."
        };

        var (same, only) = SameConnection(tests);
        var evening = same.Where(t => IsEvening(local(t.T))).ToList();
        var day = same.Where(t => IsDaytime(local(t.T))).ToList();
        if (evening.Count >= MinPerSide && day.Count >= MinPerSide)
        {
            double e = Median(evening.Select(t => t.WorstRiseMs)), d = Median(day.Select(t => t.WorstRiseMs));
            if (e - d >= 10 && e >= d * 1.5)
                parts.Add($"Evenings show more lag under load (+{e:0} ms against +{d:0} ms in daytime{only}).");
            else if (d - e >= 10 && d >= e * 1.5)
                parts.Add($"Daytime tests show more lag under load (+{d:0} ms against +{e:0} ms in the evening{only}).");
            else
                parts.Add("Evenings and daytime are about the same.");
        }

        parts.AddRange(ByConnection(tests, t => t.WorstRiseMs, v => $"+{v:0} ms"));
        return string.Join(" ", parts);
    }

    /// <param name="seen">Real-load observations, any order.</param>
    public static string RealLoad(IReadOnlyList<TestRecord> seen)
    {
        if (seen.Count == 0) return "";
        var worst = seen.MaxBy(r => r.RiseMs)!;
        return seen.Count == 1
            ? $"Seen once in real use: ping rose by {Math.Max(0, worst.RiseMs):0} ms while this PC moved about {worst.DownMbps:0} Mbps down and {worst.UpMbps:0.#} Mbps up."
            : $"Seen {seen.Count} times in real use: ping rose by a typical {Math.Max(0, Median(seen.Select(r => r.RiseMs))):0} ms during heavy traffic on this PC, at most {Math.Max(0, worst.RiseMs):0} ms.";
    }

    /// <summary>
    /// Evenings and daytime are only compared on one kind of connection: a Wi-Fi test among cable tests would make
    /// whichever time it fell in look slower. Uses the kind tested most; the note says so when others were left out.
    /// </summary>
    private static (List<TestRecord> Tests, string Note) SameConnection(IReadOnlyList<TestRecord> tests)
    {
        var groups = tests.GroupBy(t => t.Connection).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count <= 1) return (tests.ToList(), "");
        string label = groups[0].Key switch { "Wired" => "cable", "Wi-Fi" => "Wi-Fi", _ => "" };
        return (groups[0].ToList(), label.Length > 0 ? $", {label} tests only" : "");
    }

    /// <summary>"On Wi-Fi: ...; wired: ..." when both kinds of connection were tested.</summary>
    private static IEnumerable<string> ByConnection(IReadOnlyList<TestRecord> tests, Func<TestRecord, double> value, Func<double, string> format)
    {
        var wifi = tests.Where(t => t.Connection == "Wi-Fi").ToList();
        var wired = tests.Where(t => t.Connection == "Wired").ToList();
        if (wifi.Count == 0 || wired.Count == 0) yield break;
        yield return $"On Wi-Fi typically {format(Median(wifi.Select(value)))}, on a cable {format(Median(wired.Select(value)))}.";
    }
}
