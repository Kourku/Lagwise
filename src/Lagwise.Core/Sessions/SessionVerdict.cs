using System;
using System.Collections.Generic;
using System.Linq;
using Lagwise.Core.Models;
using Lagwise.Core.Statistics;

namespace Lagwise.Core.Sessions;

public enum VerdictLevel { Good, Fair, Poor, NotEnoughData }

/// <summary>Where a stretch of trouble came from, judged only from Lagwise's own measurements.</summary>
public enum TroubleCause
{
    /// <summary>The router itself answered slowly or not at all: Wi-Fi, the cable or the router.</summary>
    HomeNetwork,
    /// <summary>The router was fine but several internet servers were slow at once: the provider or the internet.</summary>
    Provider,
    /// <summary>As <see cref="Provider"/>, but while this PC was downloading or uploading heavily: its own traffic filled the line.</summary>
    PcTraffic,
    /// <summary>No probe to anything got an answer.</summary>
    Outage
}

public sealed record TroubleEpisode(DateTime StartUtc, DateTime EndUtc, TroubleCause Cause, string Text)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>What Lagwise's measurements showed around one moment the player marked as laggy.</summary>
/// <param name="Cause">The connection trouble at that moment, or null when the connection looked fine (or was not measured).</param>
/// <param name="ProcessorBusy">The connection was fine but the processor was flat out, which stutters like lag.</param>
public sealed record LagVerdict(DateTime T, string? Note, TroubleCause? Cause, string Headline, string Detail, bool ConnectionLookedFine, bool ProcessorBusy = false);

public sealed record LayerVerdict(string Name, VerdictLevel Level, string Text);

/// <summary>
/// The answer to "was it my connection?" for one recording: the player's own side of the connection, judged layer by
/// layer (home network, provider and internet, this PC), with the stretches of trouble and what each lag mark coincided
/// with. It uses only what Lagwise measured (router, reference servers, this PC's traffic and processor), the same way
/// for every game, and never claims anything about a game's server, which Lagwise cannot see.
/// </summary>
public sealed class SessionVerdict
{
    /// <summary>Length of the slots a recording is judged in.</summary>
    public static readonly TimeSpan Slot = TimeSpan.FromSeconds(5);
    /// <summary>A server unsteady in more than this share of slots is noisy by nature (ICMP rate limiting) and not counted.</summary>
    public const double NoisyShare = 0.30;
    public const double ProcessorBusyPercent = 90;
    public const int WeakWifiPercent = 30;
    /// <summary>Seconds before a lag mark, and after it, that count as "that moment" (the key is pressed after the stutter).</summary>
    public const int LagBeforeSeconds = 20, LagAfterSeconds = 5;

    public VerdictLevel Level { get; private init; }
    public string Headline { get; private init; } = "";
    public string Summary { get; private init; } = "";
    public IReadOnlyList<LayerVerdict> Layers { get; private init; } = Array.Empty<LayerVerdict>();
    public IReadOnlyList<TroubleEpisode> Episodes { get; private init; } = Array.Empty<TroubleEpisode>();
    public IReadOnlyList<LagVerdict> LagMarks { get; private init; } = Array.Empty<LagVerdict>();
    public IReadOnlyList<string> Advice { get; private init; } = Array.Empty<string>();
    /// <summary>The cause behind most of the trouble, or null when there was none.</summary>
    public TroubleCause? MainCause { get; private init; }
    public TimeSpan Measured { get; private init; }
    public TimeSpan? GameTime { get; private init; }
    public double? RouterTypicalMs { get; private init; }
    public double? InternetTypicalMs { get; private init; }
    public double? InternetLossPercent { get; private init; }
    public double OutageSeconds { get; private init; }
    public int TroubleSeconds { get; private init; }
    public int ProcessorBusySeconds { get; private init; }
    public int TrafficBusySeconds { get; private init; }
    public bool OnWifi { get; private init; }

    // ------------------------------------------------------------------ building

    private enum SlotState : byte { Unmeasured, Fine, Bad }

    private sealed class Series
    {
        public required SessionTarget Target { get; init; }
        public List<SessionSample> Samples { get; } = new();
        public double? Norm;
        public SlotState[] Slots = Array.Empty<SlotState>();
        public bool Noisy;
        public bool IsRouter => Target.Kind == MeasurementKind.LocalGateway;
    }

    /// <param name="busyBps">This PC's traffic counted as heavy (the heavy-traffic limit in Settings).</param>
    public static SessionVerdict Build(MonitoringSession s, double busyBps = SessionAnalysis.DefaultBusyBps)
    {
        var lastData = new[] { s.Samples.Count > 0 ? s.Samples.Max(x => x.T) : (DateTime?)null, s.Throughput.Count > 0 ? s.Throughput.Max(x => x.T) : null }.Max();
        var start = s.StartedUtc;
        var end = s.EndedUtc ?? lastData ?? start;
        if (lastData > end) end = lastData.Value;
        int slotCount = end > start ? (int)Math.Min(24 * 3600 / Slot.TotalSeconds * 2, Math.Ceiling((end - start) / Slot)) : 0;
        bool wifi = string.Equals(s.InterfaceType, "Wi-Fi", StringComparison.OrdinalIgnoreCase) || (s.InterfaceType?.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ?? false)
                    || s.Throughput.Any(t => t.Wifi.HasValue);

        int SlotOf(DateTime t) => (int)Math.Floor((t - start) / Slot);
        DateTime SlotStart(int i) => start + Slot * i;

        // ---- the series that matter: the router, and plain pings (or TCP connects) to internet servers
        var byTarget = s.Samples.GroupBy(x => x.TargetId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.T).ToList());
        var series = new List<Series>();
        var references = s.Targets.Where(t => t.Kind == MeasurementKind.ReferenceEndpoint).ToList();
        var plain = references.Where(t => t.Protocol is NetworkProtocol.Icmp or NetworkProtocol.Tcp).ToList();
        foreach (var t in s.Targets.Where(t => t.Kind == MeasurementKind.LocalGateway).Concat(plain.Count > 0 ? plain : references.Where(t => t.Protocol != NetworkProtocol.Dns)))
        {
            if (!byTarget.TryGetValue(t.Id, out var samples) || samples.Count == 0) continue;
            var sr = new Series { Target = t };
            sr.Samples.AddRange(samples);
            var ok = samples.Where(x => x.Ok && x.RttMs.HasValue).Select(x => x.RttMs!.Value).ToList();
            sr.Norm = ok.Count > 0 ? StatisticalCalculator.CalculateMedian(ok) : null;
            series.Add(sr);
        }

        // ---- monitoring gaps and outages, by slot
        var unmeasured = new bool[slotCount];
        foreach (var g in s.Gaps)
            for (int i = Math.Max(0, SlotOf(g.StartUtc) + 1); i <= Math.Min(slotCount - 1, SlotOf(g.EndUtc) - 1); i++) unmeasured[i] = true;
        var outage = new bool[slotCount];
        double outageSeconds = 0;
        foreach (var d in s.Disconnects)
        {
            var dEnd = d.EndUtc ?? end;
            if (dEnd <= d.StartUtc) continue;
            outageSeconds += (dEnd - d.StartUtc).TotalSeconds;
            for (int i = Math.Max(0, SlotOf(d.StartUtc)); i <= Math.Min(slotCount - 1, SlotOf(dEnd)); i++) outage[i] = true;
        }

        // ---- each series, slot by slot: unsteady when enough of its probes in the slot were lost or clearly slow
        foreach (var sr in series)
        {
            var bad = new int[slotCount];
            var all = new int[slotCount];
            foreach (var x in sr.Samples)
            {
                int i = SlotOf(x.T);
                if (i < 0 || i >= slotCount) continue;
                all[i]++;
                if (!x.Ok || !x.RttMs.HasValue || (sr.Norm is double n && x.RttMs.Value > SlowLimit(n, sr.IsRouter))) bad[i]++;
            }
            sr.Slots = new SlotState[slotCount];
            int measured = 0, unsteady = 0;
            for (int i = 0; i < slotCount; i++)
            {
                if (all[i] == 0 || unmeasured[i]) continue;
                bool isBad = bad[i] >= Math.Max(1, (int)Math.Ceiling(all[i] * 0.4));
                sr.Slots[i] = isBad ? SlotState.Bad : SlotState.Fine;
                measured++;
                if (isBad && !outage[i]) unsteady++;
            }
            sr.Noisy = measured >= 12 && unsteady > measured * NoisyShare;
        }

        var router = series.FirstOrDefault(x => x.IsRouter && !x.Noisy);
        var internet = series.Where(x => !x.IsRouter && !x.Noisy).ToList();

        // ---- this PC: heavy traffic, a busy processor, a weak Wi-Fi signal
        var heavy = new int[slotCount];
        var cpuSum = new double[slotCount];
        var cpuN = new int[slotCount];
        var wifiMin = new int?[slotCount];
        foreach (var p in s.Throughput)
        {
            int i = SlotOf(p.T);
            if (i < 0 || i >= slotCount) continue;
            if (Math.Max(p.Rx, p.Tx) >= busyBps) heavy[i]++;
            if (p.Cpu is double c) { cpuSum[i] += c; cpuN[i]++; }
            if (p.Wifi is int w) wifiMin[i] = wifiMin[i] is int m ? Math.Min(m, w) : w;
        }
        bool Busy(int i) => heavy[i] >= SessionAnalysis.BusyMinSeconds;
        bool CpuBusy(int i) => cpuN[i] > 0 && cpuSum[i] / cpuN[i] >= ProcessorBusyPercent;
        bool WeakWifi(int i) => wifiMin[i] is int w && w < WeakWifiPercent;

        // ---- the cause of each slot
        var cause = new TroubleCause?[slotCount];
        var judged = new bool[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            if (unmeasured[i]) continue;
            if (outage[i]) { cause[i] = TroubleCause.Outage; judged[i] = true; continue; }
            var r = router?.Slots[i] ?? SlotState.Unmeasured;
            var inet = internet.Select(x => x.Slots[i]).Where(x => x != SlotState.Unmeasured).ToList();
            if (r == SlotState.Unmeasured && inet.Count == 0) continue;
            judged[i] = true;
            if (r == SlotState.Bad) cause[i] = TroubleCause.HomeNetwork;
            else if (inet.Count > 0 && inet.Count(x => x == SlotState.Bad) >= inet.Count / 2 + 1)
                cause[i] = Busy(i) ? TroubleCause.PcTraffic : TroubleCause.Provider;
        }

        // ---- stretches of trouble (one calm slot between two bad ones does not split a stretch)
        var episodes = new List<(int From, int To, TroubleCause Cause)>();
        for (int i = 0; i < slotCount; i++)
        {
            if (cause[i] is not TroubleCause c) continue;
            if (episodes.Count > 0 && episodes[^1].Cause == c && i - episodes[^1].To <= 2) episodes[^1] = (episodes[^1].From, i, c);
            else episodes.Add((i, i, c));
        }

        int judgedSlots = judged.Count(x => x);
        int troubleSlots = cause.Count(x => x.HasValue);
        var measuredSpan = TimeSpan.FromTicks(Slot.Ticks * judgedSlots);
        int slotSeconds = (int)Slot.TotalSeconds;

        // ---- numbers people recognise
        double? routerTypical = router?.Norm ?? series.FirstOrDefault(x => x.IsRouter)?.Norm;
        var internetOk = internet.SelectMany(x => x.Samples.Where(y => y.Ok && y.RttMs.HasValue).Select(y => y.RttMs!.Value)).ToList();
        double? internetTypical = internetOk.Count > 0 ? StatisticalCalculator.CalculateMedian(internet.Where(x => x.Norm.HasValue).Select(x => x.Norm!.Value).ToList()) : null;
        int internetProbes = internet.Sum(x => x.Samples.Count(y => !InGap(s, y.T)));
        int internetLost = internet.Sum(x => x.Samples.Count(y => !y.Ok && !InGap(s, y.T)));
        double? internetLoss = internetProbes > 0 ? internetLost * 100.0 / internetProbes : null;

        TimeSpan? gameTime = null;
        if (s.GameProcesses.Count > 0)
        {
            var from = s.GameProcesses.Min(p => p.FirstSeenUtc);
            var to = s.GameProcesses.Max(p => p.ExitedUtc ?? p.LastSeenUtc);
            if (to > from) gameTime = to - from;
        }

        var episodeList = episodes.Select(e => new TroubleEpisode(SlotStart(e.From), SlotStart(e.To + 1), e.Cause,
            DescribeEpisode(e.Cause, SlotStart(e.From), SlotStart(e.To + 1), Enumerable.Range(e.From, e.To - e.From + 1).Any(WeakWifi), wifi))).ToList();

        // ---- verdict
        var networkEpisodes = episodeList.Where(e => e.Cause != TroubleCause.Outage).ToList();
        double hours = Math.Max(measuredSpan.TotalHours, 1.0 / 60);
        double troubleShare = judgedSlots > 0 ? troubleSlots / (double)judgedSlots : 0;
        bool enough = judgedSlots * Slot.TotalSeconds >= 30;
        VerdictLevel level;
        if (!enough) level = VerdictLevel.NotEnoughData;
        else if (outageSeconds >= 10 || troubleShare >= 0.05 || networkEpisodes.Count / hours >= 12) level = VerdictLevel.Poor;
        else if (outageSeconds > 0 || networkEpisodes.Count / hours >= 2 || networkEpisodes.Any(e => e.Duration.TotalSeconds >= 15)) level = VerdictLevel.Fair;
        else level = VerdictLevel.Good;

        TroubleCause? main = episodeList.Count == 0 ? null
            : episodeList.GroupBy(e => e.Cause).OrderByDescending(g => g.Key == TroubleCause.Outage && outageSeconds >= 10)
                .ThenByDescending(g => g.Sum(e => e.Duration.TotalSeconds)).First().Key;

        // ---- lag marks
        var lagMarks = s.LagMarkers.OrderBy(m => m.T).Select(m => JudgeLag(s, m, router, internet, cause, unmeasured, SlotOf, slotCount, busyBps, wifi)).ToList();

        int busySlots = Enumerable.Range(0, slotCount).Count(Busy);
        int cpuSlots = Enumerable.Range(0, slotCount).Count(CpuBusy);
        var layers = new List<LayerVerdict>
        {
            HomeLayer(router, series.FirstOrDefault(x => x.IsRouter), episodeList, wifi, enough),
            InternetLayer(internet, series.Where(x => !x.IsRouter).ToList(), episodeList, internetTypical, internetLoss, outageSeconds, enough, s.Vpn),
            PcLayer(busySlots * slotSeconds, cpuSlots * slotSeconds, cpuN.Any(n => n > 0), s.Throughput.Count > 0, episodeList)
        };

        var (headline, summary) = Words(level, main, episodeList, internetTypical, routerTypical, wifi, outageSeconds, measuredSpan, lagMarks, s.Vpn);
        return new SessionVerdict
        {
            Level = level,
            Headline = headline,
            Summary = summary,
            Layers = layers,
            Episodes = episodeList,
            LagMarks = lagMarks,
            Advice = AdviceFor(episodeList, lagMarks, wifi, s.Vpn),
            MainCause = level is VerdictLevel.Good or VerdictLevel.NotEnoughData ? null : main,
            Measured = measuredSpan,
            GameTime = gameTime,
            RouterTypicalMs = routerTypical,
            InternetTypicalMs = internetTypical,
            InternetLossPercent = internetLoss,
            OutageSeconds = outageSeconds,
            TroubleSeconds = troubleSlots * slotSeconds,
            ProcessorBusySeconds = cpuSlots * slotSeconds,
            TrafficBusySeconds = busySlots * slotSeconds,
            OnWifi = wifi
        };
    }

    /// <summary>
    /// The verdict in two or three words, for a list column: "Steady", "Wi-Fi or router", "Beyond your router"... Falls
    /// back to the headline itself for wording this version does not know.
    /// </summary>
    public static string ShortLabel(string? headline) => headline switch
    {
        null or "" => "",
        "Your connection was steady" => "Steady",
        "Too short to judge" => "Too short to judge",
        "Your connection dropped out" => "Dropped out",
        "Your connection dropped briefly" => "Dropped briefly",
        "Lag came from your Wi-Fi or router" => "Wi-Fi or router",
        "Lag came from your home network" => "Home network",
        "Lag came from this PC's own downloads" => "This PC's downloads",
        "Lag came from beyond your router" => "Beyond your router",
        _ => headline
    };

    /// <summary>Clearly slower than the target's own norm in this recording: the router by 30 ms (or 3x), a server by 50 ms (or 1.5x).</summary>
    public static double SlowLimit(double norm, bool router) =>
        router ? Math.Max(norm * 3, norm + 30) : norm + Math.Max(50, norm * 0.5);

    private static bool InGap(MonitoringSession s, DateTime t) => s.Gaps.Any(g => t > g.StartUtc && t < g.EndUtc);

    // ------------------------------------------------------------------ lag marks

    private static LagVerdict JudgeLag(MonitoringSession s, LagMarker m, Series? router, List<Series> internet,
        TroubleCause?[] cause, bool[] unmeasured, Func<DateTime, int> slotOf, int slotCount, double busyBps, bool wifi)
    {
        var from = m.T.AddSeconds(-LagBeforeSeconds);
        var to = m.T.AddSeconds(LagAfterSeconds);
        int a = Math.Max(0, slotOf(from)), b = Math.Min(slotCount - 1, slotOf(to));
        var slots = a <= b ? Enumerable.Range(a, b - a + 1).ToList() : new List<int>();

        string Around(Series? sr)
        {
            if (sr?.Norm is not double norm) return "";
            var w = sr.Samples.Where(x => x.T >= from && x.T <= to).ToList();
            if (w.Count == 0) return "";
            var ok = w.Where(x => x.Ok && x.RttMs.HasValue).Select(x => x.RttMs!.Value).ToList();
            int lost = w.Count - ok.Count;
            string worst = ok.Count > 0 ? $"up to {ok.Max():F0} ms" : "no answers";
            return $"{worst}, usually {norm:F0} ms{(lost > 0 && ok.Count > 0 ? $", {lost} of {w.Count} probes lost" : "")}";
        }
        Series? WorstInternet() => internet.Where(x => x.Norm.HasValue)
            .OrderByDescending(x => x.Samples.Where(y => y.T >= from && y.T <= to).Select(y => y.Ok && y.RttMs.HasValue ? y.RttMs.Value / x.Norm!.Value : 10).DefaultIfEmpty(0).Max())
            .FirstOrDefault();

        var traffic = s.Throughput.Where(p => p.T >= from && p.T <= to).ToList();
        double peakDown = traffic.Count > 0 ? traffic.Max(p => p.Rx) : 0, peakUp = traffic.Count > 0 ? traffic.Max(p => p.Tx) : 0;
        bool busy = traffic.Count(p => Math.Max(p.Rx, p.Tx) >= busyBps) >= SessionAnalysis.BusyMinSeconds;
        var cpu = traffic.Where(p => p.Cpu.HasValue).Select(p => p.Cpu!.Value).ToList();
        double? cpuPeak = cpu.Count > 0 ? cpu.Max() : null;
        bool cpuBusy = cpu.Count > 0 && cpu.Count(c => c >= ProcessorBusyPercent) >= Math.Max(2, cpu.Count / 4);
        string trafficText = traffic.Count == 0 ? "" : $"{Services.AdapterActivityMonitor.FormatBps(peakDown)} down and {Services.AdapterActivityMonitor.FormatBps(peakUp)} up at most";

        if (slots.Count == 0 || slots.All(i => unmeasured[i]))
            return new LagVerdict(m.T, m.Note, null, "Lagwise was not measuring at that moment", "The PC was asleep or Lagwise was paused, so there is nothing to compare.", false);

        var causes = slots.Select(i => cause[i]).Where(c => c.HasValue).Select(c => c!.Value).ToList();
        TroubleCause? c0 = causes.Contains(TroubleCause.Outage) ? TroubleCause.Outage
            : causes.Contains(TroubleCause.HomeNetwork) ? TroubleCause.HomeNetwork
            : causes.Contains(TroubleCause.PcTraffic) ? TroubleCause.PcTraffic
            : causes.Contains(TroubleCause.Provider) ? (busy ? TroubleCause.PcTraffic : TroubleCause.Provider)
            : null;

        switch (c0)
        {
            case TroubleCause.Outage:
                return new LagVerdict(m.T, m.Note, c0, "Your connection was down", "No probe to your router or to any internet server got an answer at that moment.", false);
            case TroubleCause.HomeNetwork:
                return new LagVerdict(m.T, m.Note, c0, wifi ? "Your Wi-Fi or router was slow" : "Your home network was slow",
                    $"Your router itself answered slowly: {Around(router)}. The trouble was between this PC and the router" + (wifi ? ", which on Wi-Fi is usually interference or distance." : ": the router, a cable, or a repeater or mesh unit and its Wi-Fi link."), false);
            case TroubleCause.PcTraffic:
                return new LagVerdict(m.T, m.Note, c0, "This PC's own downloads or uploads filled the line",
                    $"Internet servers were slow ({Around(WorstInternet())}) while this PC moved {trafficText}. That traffic queues in front of the game's.", false);
            case TroubleCause.Provider:
                return new LagVerdict(m.T, m.Note, c0, "The delay was beyond your router",
                    $"Several internet servers were slow at once ({Around(WorstInternet())}) while your router answered normally{(router != null ? $" ({Around(router)})" : "")}. That points at {BeyondRouter(s.Vpn)}.", false);
        }

        if (cpuBusy)
            return new LagVerdict(m.T, m.Note, null, "Your connection was fine; this PC was very busy",
                $"The processor was at up to {cpuPeak:F0}%. A PC working that hard stutters in a way that feels like lag.", true, ProcessorBusy: true);
        bool gameOffline = s.GameProcesses.Count > 0 && s.Connections.Count == 0;
        if (gameOffline)
            return new LagVerdict(m.T, m.Note, null, "Your connection was fine, and the game was not online",
                "The game had no network connections Lagwise could see, so this stutter was not network lag.", true);
        string router0 = router != null ? $"Your router: {Around(router)}. " : "";
        var inet0 = WorstInternet();
        string inetText = inet0 != null ? $"Internet servers: {Around(inet0)}. " : "";
        return new LagVerdict(m.T, m.Note, null, "Your connection looked normal at that moment",
            $"{router0}{inetText}The lag was probably further away (the game's server or the route to it) or in the game itself. Lagwise cannot measure the game's server.", true);
    }

    // ------------------------------------------------------------------ words

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    private static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };

    private static string Span(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s"
        : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    private static string DescribeEpisode(TroubleCause c, DateTime from, DateTime to, bool weakWifi, bool wifi) => c switch
    {
        TroubleCause.Outage => $"{Clock(from)}: the connection was down for {Span(to - from)}.",
        TroubleCause.HomeNetwork => $"{Clock(from)}: your {(wifi ? "Wi-Fi or router" : "home network")} was slow for {Span(to - from)}{(weakWifi ? " (weak Wi-Fi signal)" : "")}.",
        TroubleCause.PcTraffic => $"{Clock(from)}: this PC's own traffic slowed the connection for {Span(to - from)}.",
        _ => $"{Clock(from)}: slow beyond your router for {Span(to - from)}."
    };

    private static (string Headline, string Summary) Words(VerdictLevel level, TroubleCause? main, List<TroubleEpisode> episodes,
        double? internetMs, double? routerMs, bool wifi, double outageSeconds, TimeSpan measured, List<LagVerdict> lags, string? vpn)
    {
        string numbers = internetMs is double ms ? $"Internet servers answered in {ms:F0} ms typically" + (routerMs is double r ? $", your router in {r:F0} ms" : "") + "." : "";
        int fineLags = lags.Count(l => l.ConnectionLookedFine);
        string lagNote = lags.Count == 0 ? ""
            : fineLags == lags.Count ? $" Your connection looked normal at {(lags.Count == 1 ? "the moment" : $"all {lags.Count} moments")} you marked lag, so that lag came from further away or from the game."
            : $" {lags.Count - fineLags} of the {lags.Count} moments you marked lag coincided with trouble on your side.";

        if (level == VerdictLevel.NotEnoughData)
            return ("Too short to judge", $"Lagwise measured for {Span(measured)}; a verdict needs at least half a minute of measurements.");
        if (level == VerdictLevel.Good)
            return ("Your connection was steady",
                $"{numbers} {(episodes.Count == 0 ? "No lag spikes on your side." : episodes.Count == 1 ? "Just one short spike." : $"Only {episodes.Count} short spikes.")}{lagNote}".Trim());

        int n = episodes.Count(e => e.Cause == main);
        string total = Span(TimeSpan.FromSeconds(episodes.Where(e => e.Cause == main).Sum(e => e.Duration.TotalSeconds)));
        string headline = main switch
        {
            TroubleCause.Outage => outageSeconds >= 60 ? "Your connection dropped out" : "Your connection dropped briefly",
            TroubleCause.HomeNetwork => wifi ? "Lag came from your Wi-Fi or router" : "Lag came from your home network",
            TroubleCause.PcTraffic => "Lag came from this PC's own downloads",
            _ => "Lag came from beyond your router"
        };
        string what = main switch
        {
            TroubleCause.Outage => $"Nothing got an answer for {Span(TimeSpan.FromSeconds(outageSeconds))} in all.",
            TroubleCause.HomeNetwork => $"Your router itself was slow {Times(n)} ({total}), so the trouble was between this PC and the router.",
            TroubleCause.PcTraffic => $"The connection was slow {Times(n)} ({total}) while this PC was downloading or uploading heavily.",
            _ => $"Several internet servers were slow at the same time {Times(n)} ({total}) while your router was fine: {BeyondRouter(vpn)}."
        };
        // trouble of another kind as well, in a word
        var others = episodes.Where(e => e.Cause != main).GroupBy(e => e.Cause).OrderByDescending(g => g.Sum(e => e.Duration.TotalSeconds))
            .Select(g => (g.Key switch
            {
                TroubleCause.Outage => "the connection dropped",
                TroubleCause.HomeNetwork => wifi ? "your Wi-Fi or router was slow" : "your home network was slow",
                TroubleCause.PcTraffic => "this PC's own downloads slowed it",
                _ => "it was slow beyond your router"
            }) + " " + Times(g.Count())).ToList();
        string also = others.Count == 0 ? "" : $" Also, {string.Join(" and ", others)}.";
        return (headline, $"{what}{also} {numbers}{lagNote}".Trim());
    }

    private static LayerVerdict HomeLayer(Series? router, Series? anyRouter, List<TroubleEpisode> episodes, bool wifi, bool enough)
    {
        string name = wifi ? "Wi-Fi and router" : "Home network";
        if (anyRouter == null) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "Your router was not measured, so this part could not be judged.");
        if (router == null) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "Your router does not answer pings reliably (many routers give them low priority), so this part could not be judged.");
        if (!enough) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "Not measured long enough yet.");
        var mine = episodes.Where(e => e.Cause == TroubleCause.HomeNetwork).ToList();
        string typical = router.Norm is double n ? $"Your router answered in {n:F0} ms typically" : "Your router answered";
        if (mine.Count == 0) return new LayerVerdict(name, VerdictLevel.Good, $"{typical}, steadily.");
        var secs = mine.Sum(e => e.Duration.TotalSeconds);
        return new LayerVerdict(name, mine.Count >= 3 || secs > 60 ? VerdictLevel.Poor : VerdictLevel.Fair,
            $"{typical}, but was slow {Times(mine.Count)} ({Span(TimeSpan.FromSeconds(secs))}).");
    }

    /// <summary>Where trouble beyond the router lies: with a VPN in the way, the VPN first.</summary>
    private static string BeyondRouter(string? vpn) =>
        vpn == null ? "your provider or the internet beyond it" : $"the VPN your traffic went through ({vpn}), your provider, or the internet beyond them";

    private static LayerVerdict InternetLayer(List<Series> internet, List<Series> anyInternet, List<TroubleEpisode> episodes, double? typicalMs, double? loss, double outageSeconds, bool enough,
        string? vpn)
    {
        string name = vpn == null ? "Provider and internet" : "VPN, provider and internet";
        if (anyInternet.Count == 0) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "No internet server was measured.");
        if (internet.Count == 0) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "The internet servers did not answer pings reliably, so this part could not be judged.");
        if (!enough) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "Not measured long enough yet.");
        var mine = episodes.Where(e => e.Cause is TroubleCause.Provider or TroubleCause.Outage).ToList();
        string typical = typicalMs is double ms ? $"Internet servers answered in {ms:F0} ms typically" : "Internet servers answered";
        string lossText = loss is double l && l >= 0.05 ? $", {l:F1}% of probes lost" : "";
        string outage = outageSeconds > 0 ? $" The connection was down for {Span(TimeSpan.FromSeconds(outageSeconds))}." : "";
        if (mine.Count == 0) return new LayerVerdict(name, VerdictLevel.Good, $"{typical}{lossText}, with no slow moments.");
        var secs = mine.Where(e => e.Cause == TroubleCause.Provider).Sum(e => e.Duration.TotalSeconds);
        int slow = mine.Count(e => e.Cause == TroubleCause.Provider);
        var level = outageSeconds >= 10 || slow >= 3 || secs > 60 ? VerdictLevel.Poor : VerdictLevel.Fair;
        return new LayerVerdict(name, level, $"{typical}{lossText}." + (slow > 0 ? $" Slow beyond your router {Times(slow)} ({Span(TimeSpan.FromSeconds(secs))})." : "") + outage
            + (vpn != null && slow > 0 ? $" Your traffic went through a VPN ({vpn}), which is part of this stretch." : ""));
    }

    private static LayerVerdict PcLayer(int busySeconds, int cpuSeconds, bool hasCpu, bool hasTraffic, List<TroubleEpisode> episodes)
    {
        const string name = "This PC";
        if (!hasTraffic) return new LayerVerdict(name, VerdictLevel.NotEnoughData, "This recording has no data about this PC's own activity.");
        var parts = new List<string>();
        int slowedBy = episodes.Count(e => e.Cause == TroubleCause.PcTraffic);
        parts.Add(busySeconds == 0 ? "No heavy downloads or uploads." : $"Downloading or uploading heavily for {Span(TimeSpan.FromSeconds(busySeconds))}" + (slowedBy > 0 ? $", which slowed the connection {Times(slowedBy)}." : ", without slowing the connection."));
        if (hasCpu) parts.Add(cpuSeconds == 0 ? "The processor never ran flat out." : $"The processor was at {ProcessorBusyPercent:F0}% or more for {Span(TimeSpan.FromSeconds(cpuSeconds))}, which can feel like lag.");
        var level = slowedBy > 0 ? (slowedBy >= 3 ? VerdictLevel.Poor : VerdictLevel.Fair) : cpuSeconds >= 60 ? VerdictLevel.Fair : VerdictLevel.Good;
        return new LayerVerdict(name, level, string.Join(" ", parts));
    }

    private static IReadOnlyList<string> AdviceFor(List<TroubleEpisode> episodes, List<LagVerdict> lags, bool wifi, string? vpn)
    {
        var causes = episodes.Select(e => e.Cause).Concat(lags.Where(l => l.Cause.HasValue).Select(l => l.Cause!.Value)).ToHashSet();
        var advice = new List<string>();
        if (causes.Contains(TroubleCause.HomeNetwork))
            advice.Add(wifi ? "Play on a network cable if you can; otherwise move closer to the router or away from walls and other wireless devices. Wi-Fi is the most common cause of lag spikes at home."
                            : "Restart the router, and check the network cable and its plugs at both ends. If a repeater or mesh unit links this PC to the router, move it closer to the router or connect it to the router by cable: its Wi-Fi link is part of your home network.");
        if (causes.Contains(TroubleCause.PcTraffic))
            advice.Add("Pause downloads, game updates and cloud backup while you play. The Network activity page shows which programs use the connection.");
        if (causes.Contains(TroubleCause.Provider) && vpn != null)
            advice.Add($"Your traffic went through a VPN ({vpn}). Try a VPN server closer to you or to the game's region, or play once without the VPN and compare.");
        if (causes.Contains(TroubleCause.Provider))
            advice.Add("If this keeps happening, especially in the evening, record a few more sessions and send the report to your provider (Export report on the recording).");
        if (causes.Contains(TroubleCause.Outage))
            advice.Add("Check the router's lights and the line to the modem when it drops. If drops keep coming, send the report to your provider.");
        if (lags.Any(l => l.ProcessorBusy))
            advice.Add("Close programs running in the background, or lower the game's graphics settings: the processor was flat out when you marked lag.");
        if (advice.Count == 0 && lags.Count > 0 && lags.All(l => l.ConnectionLookedFine))
            advice.Add("Your side of the connection was fine when you marked lag. Check the game's own ping or network display to see whether its server was the problem.");
        return advice;
    }
}
