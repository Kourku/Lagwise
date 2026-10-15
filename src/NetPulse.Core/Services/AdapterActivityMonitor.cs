using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace NetPulse.Core.Services;

/// <summary>Raw, ever-increasing counters of one network adapter, as Windows reports them.</summary>
public sealed record AdapterCounters(
    string Id, string Name, string Description, string Type, long SpeedBps,
    long BytesReceived, long BytesSent, long PacketsReceived, long PacketsSent,
    long Errors, long Discards);

/// <summary>One second of adapter activity: throughput in bits per second and packets per second.</summary>
public sealed record ActivityPoint(DateTime T, double RxBps, double TxBps, double RxPps, double TxPps);

/// <summary>What the Network activity page shows for one adapter.</summary>
public sealed class AdapterActivity
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public long SpeedBps { get; init; }
    public IReadOnlyList<ActivityPoint> History { get; init; } = Array.Empty<ActivityPoint>();
    public ActivityPoint? Latest => History.Count > 0 ? History[^1] : null;
    /// <summary>Data moved since NetPulse started watching this adapter.</summary>
    public long BytesReceivedSinceStart { get; init; }
    public long BytesSentSinceStart { get; init; }
    /// <summary>Receive and send errors plus discarded packets since NetPulse started watching (not since Windows started).</summary>
    public long ErrorsSinceStart { get; init; }
    public long DiscardsSinceStart { get; init; }
    public DateTime WatchingSinceUtc { get; init; }

    public double PeakRx(TimeSpan window, DateTime now) => Window(window, now).Select(p => p.RxBps).DefaultIfEmpty(0).Max();
    public double PeakTx(TimeSpan window, DateTime now) => Window(window, now).Select(p => p.TxBps).DefaultIfEmpty(0).Max();
    public double AverageRx(TimeSpan window, DateTime now) => Window(window, now).Select(p => p.RxBps).DefaultIfEmpty(0).Average();
    public double AverageTx(TimeSpan window, DateTime now) => Window(window, now).Select(p => p.TxBps).DefaultIfEmpty(0).Average();
    public IEnumerable<ActivityPoint> Window(TimeSpan window, DateTime now) => History.Where(p => now - p.T <= window);

    /// <summary>Share of the adapter's link speed in use (the larger direction), 0..1; null when Windows reports no link speed.</summary>
    public double? Utilization => SpeedBps > 0 && Latest is { } l ? Math.Min(1, Math.Max(l.RxBps, l.TxBps) / SpeedBps) : null;
}

/// <summary>A stretch of heavy traffic on this PC, reported once when it has lasted long enough.</summary>
public sealed record HeavyTraffic(string Adapter, double RxBps, double TxBps, TimeSpan Duration);

/// <summary>
/// Network adapter activity, like the Performance tab of Task Manager: reads each adapter's byte, packet and error
/// counters once a second and turns the differences into speeds. It sees all traffic of this PC on that adapter
/// (games, downloads, updates, streaming), not traffic of other devices at home, and not which program caused it.
/// </summary>
public sealed class AdapterActivityMonitor
{
    /// <summary>Kept for the longest chart window.</summary>
    public static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(15);

    /// <summary>Traffic at least this fast (either direction) for <see cref="HeavyTrafficAfter"/> counts as heavy.</summary>
    public double HeavyTrafficBps { get; set; } = 15_000_000;
    public TimeSpan HeavyTrafficAfter { get; set; } = TimeSpan.FromSeconds(10);

    private sealed class State
    {
        public AdapterCounters First = null!;
        public AdapterCounters Last = null!;
        public DateTime LastT;
        public DateTime FirstT;
        public readonly List<ActivityPoint> History = new();
        public DateTime? HeavySince;
        public bool HeavyReported;
        public double HeavyPeakRx, HeavyPeakTx;
    }

    private readonly Func<IReadOnlyList<AdapterCounters>> _read;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Raised once per stretch of heavy traffic (unless <see cref="SuppressHeavyTraffic"/> says it is NetPulse's own test).</summary>
    public event EventHandler<HeavyTraffic>? HeavyTrafficDetected;
    public event EventHandler? Sampled;

    /// <summary>True while NetPulse itself is generating load (speed and bufferbloat tests), which must not be reported.</summary>
    public Func<bool> SuppressHeavyTraffic { get; set; } = () => false;

    public AdapterActivityMonitor(Func<IReadOnlyList<AdapterCounters>>? read = null)
    {
        _read = read ?? (() => OperatingSystem.IsWindows() ? ReadWindowsCounters() : Array.Empty<AdapterCounters>());
    }

    /// <summary>Takes one reading. Called once a second by the host; tests call it with their own clock.</summary>
    public void Sample(DateTime nowUtc)
    {
        IReadOnlyList<AdapterCounters> counters;
        try { counters = _read(); }
        catch (Exception) { return; } // adapters can vanish mid-enumeration; the next reading will do

        var reports = new List<HeavyTraffic>();
        lock (_lock)
        {
            foreach (var c in counters)
            {
                if (!_states.TryGetValue(c.Id, out var s))
                {
                    _states[c.Id] = new State { First = c, Last = c, FirstT = nowUtc, LastT = nowUtc };
                    continue;
                }

                double sec = (nowUtc - s.LastT).TotalSeconds;
                long rx = c.BytesReceived - s.Last.BytesReceived, tx = c.BytesSent - s.Last.BytesSent;
                // a counter that went backwards was reset (adapter re-enabled, driver reload): start counting again from here
                if (rx < 0 || tx < 0 || c.PacketsReceived < s.Last.PacketsReceived || c.PacketsSent < s.Last.PacketsSent)
                {
                    s.First = c; s.FirstT = nowUtc; s.Last = c; s.LastT = nowUtc;
                    continue;
                }
                if (sec < 0.2) continue;

                var p = new ActivityPoint(nowUtc, rx * 8 / sec, tx * 8 / sec,
                    (c.PacketsReceived - s.Last.PacketsReceived) / sec, (c.PacketsSent - s.Last.PacketsSent) / sec);
                s.History.Add(p);
                s.History.RemoveAll(h => nowUtc - h.T > HistoryLength);
                s.Last = c;
                s.LastT = nowUtc;

                if (TrackHeavy(s, p, nowUtc) is { } heavy) reports.Add(heavy with { Adapter = c.Name });
            }

            // adapters that disappeared (unplugged, disabled) are forgotten
            var present = counters.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _states.Keys.Where(k => !present.Contains(k)).ToList()) _states.Remove(gone);
        }

        foreach (var r in reports) HeavyTrafficDetected?.Invoke(this, r);
        Sampled?.Invoke(this, EventArgs.Empty);
    }

    private HeavyTraffic? TrackHeavy(State s, ActivityPoint p, DateTime now)
    {
        bool heavy = Math.Max(p.RxBps, p.TxBps) >= HeavyTrafficBps && !SuppressHeavyTraffic();
        if (!heavy)
        {
            s.HeavySince = null;
            s.HeavyReported = false;
            s.HeavyPeakRx = s.HeavyPeakTx = 0;
            return null;
        }
        s.HeavySince ??= p.T;
        s.HeavyPeakRx = Math.Max(s.HeavyPeakRx, p.RxBps);
        s.HeavyPeakTx = Math.Max(s.HeavyPeakTx, p.TxBps);
        var lasted = now - s.HeavySince.Value;
        if (s.HeavyReported || lasted < HeavyTrafficAfter) return null;
        s.HeavyReported = true;   // once per stretch; it re-arms after traffic drops back
        return new HeavyTraffic("", s.HeavyPeakRx, s.HeavyPeakTx, lasted);
    }

    /// <summary>All adapters seen in the latest reading, busiest-capable first (the one with a default route is chosen by the page).</summary>
    public IReadOnlyList<AdapterActivity> Snapshot()
    {
        lock (_lock)
        {
            return _states.Values.Select(s => new AdapterActivity
            {
                Id = s.Last.Id,
                Name = s.Last.Name,
                Description = s.Last.Description,
                Type = s.Last.Type,
                SpeedBps = s.Last.SpeedBps,
                History = s.History.ToList(),
                BytesReceivedSinceStart = s.Last.BytesReceived - s.First.BytesReceived,
                BytesSentSinceStart = s.Last.BytesSent - s.First.BytesSent,
                ErrorsSinceStart = Math.Max(0, s.Last.Errors - s.First.Errors),
                DiscardsSinceStart = Math.Max(0, s.Last.Discards - s.First.Discards),
                WatchingSinceUtc = s.FirstT
            }).OrderByDescending(a => a.SpeedBps).ToList();
        }
    }

    /// <summary>Up, non-loopback, non-tunnel adapters (the ones Task Manager shows).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IReadOnlyList<AdapterCounters> ReadWindowsCounters()
    {
        var list = new List<AdapterCounters>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var st = nic.GetIPStatistics();   // IPv4 and IPv6 together
                list.Add(new AdapterCounters(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType.ToString(), Math.Max(0, nic.Speed),
                    st.BytesReceived, st.BytesSent,
                    st.UnicastPacketsReceived + st.NonUnicastPacketsReceived, st.UnicastPacketsSent + st.NonUnicastPacketsSent,
                    st.IncomingPacketsWithErrors + st.OutgoingPacketsWithErrors,
                    st.IncomingPacketsDiscarded + st.OutgoingPacketsDiscarded));
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }
        return list;
    }

    /// <summary>"0 bps", "850 Kbps", "12.4 Mbps", "1.2 Gbps".</summary>
    public static string FormatBps(double bps) => bps switch
    {
        >= 1e9 => $"{bps / 1e9:0.##} Gbps",
        >= 1e7 => $"{bps / 1e6:0} Mbps",
        >= 1e6 => $"{bps / 1e6:0.#} Mbps",
        >= 1e3 => $"{bps / 1e3:0} Kbps",
        _ => $"{bps:0} bps"
    };

    /// <summary>"512 KB", "48.2 MB", "3.1 GB".</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.##} GB",
        >= 1_000_000 => $"{bytes / 1e6:0.#} MB",
        >= 1_000 => $"{bytes / 1e3:0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>The reference ping as one value per second, for drawing over the traffic graph.</summary>
public static class ReferencePing
{
    /// <summary>
    /// Typical (median) round trip of the plain-ping reference servers in each second since <paramref name="fromUtc"/>.
    /// Only ICMP reference targets count: the router, DNS lookups and HTTPS handshakes measure other things.
    /// </summary>
    public static IReadOnlyList<(DateTime T, double Ms)> PerSecond(IEnumerable<Models.Measurement> history, IReadOnlySet<string> referenceIds, DateTime fromUtc)
    {
        return history
            .Where(m => m.Success && m.Timestamp >= fromUtc && m.Protocol == Models.NetworkProtocol.Icmp && referenceIds.Contains(m.TargetId))
            .GroupBy(m => new DateTime(m.Timestamp.Ticks - m.Timestamp.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.AddMilliseconds(500), Statistics.StatisticalCalculator.CalculateMedian(g.Select(m => m.RttMs).ToList())))
            .ToList();
    }
}
