using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

public class MeasurementEngine
{
    /// <summary>Consecutive failed probes before a gateway/DNS outage is declared.</summary>
    private const int OutageFailureThreshold = 2;
    /// <summary>Consecutive cycles without a single successful probe before the Internet is declared unreachable.</summary>
    private const int DisconnectCycleThreshold = 3;
    /// <summary>Minimum probes in the recent window before a loss event may fire.</summary>
    private const int MinSamplesForLossEvent = 10;
    /// <summary>Samples needed before a target's own typical latency is trusted as a baseline.</summary>
    private const int MinSamplesForBaseline = 20;

    private readonly NetworkDiscoveryService _discoveryService;
    private readonly PingService _pingService;
    private readonly TcpProbeService _tcpService;
    private readonly HttpProbeService _httpService;
    private readonly DnsProbeService _dnsService;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly EventLogService _eventLogService;

    private readonly ConcurrentDictionary<string, TargetMetrics> _metrics = new();
    private readonly ConcurrentDictionary<string, double> _previousLatencies = new();
    private readonly ConcurrentDictionary<string, TargetConfig> _dynamicTargets = new();
    private readonly Dictionary<string, RollingWindow> _windows = new();
    private readonly Dictionary<string, int> _consecutiveFailures = new();
    private readonly Dictionary<string, DateTime> _firstFailureUtc = new();
    private readonly EpisodeTracker _episodes = new();
    private readonly List<Measurement> _allHistory = new();
    private readonly object _historyLock = new();
    private readonly object _ingestLock = new();

    private int _zeroSuccessCycles;
    private DateTime? _firstFailedCycleUtc;
    private bool _internetDown;
    /// <summary>An internet server answered at least once: from then on, internet servers all going silent is an outage.</summary>
    private bool _internetSeenUp;
    /// <summary>The router answered during the current run of failed cycles.</summary>
    private bool _routerAnsweredInFailedRun;

    /// <summary>Marks a "Disconnected" event whose break was beyond the router (the router kept answering).</summary>
    public const string BeyondRouterContext = "beyond-router";
    /// <summary>Marks a "Disconnected" event where the router did not answer either.</summary>
    public const string LocalContext = "local";

    /// <summary>True while the internet is considered unreachable (between a "Disconnected" and its "Restored").</summary>
    public bool IsInternetDown { get { lock (_ingestLock) return _internetDown; } }

    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;

    public AppSettings Settings { get; set; } = AppSettings.CreateDefault();
    public NetworkInterfaceInfo CurrentNic { get; private set; } = new();
    public DiagnosticResult CurrentDiagnosis { get; private set; } = new();
    public bool IsRunning { get; private set; }

    public event EventHandler<IReadOnlyList<TargetMetrics>>? MetricsUpdated;
    public event EventHandler<DiagnosticResult>? DiagnosisUpdated;
    public event EventHandler<NetworkInterfaceInfo>? NetworkInterfaceUpdated;
    /// <summary>Raised for every processed probe result (used by the session recorder).</summary>
    public event EventHandler<Measurement>? MeasurementProcessed;
    /// <summary>Long-term "usual" latency per target, remembered across restarts. Null: only the recent window is used.</summary>
    public BaselineMemory? Baselines { get; set; }

    /// <summary>Stores each target's current typical latency in <see cref="Baselines"/> (called every <see cref="BaselineMemory.SampleInterval"/>).</summary>
    public void SampleBaselines(DateTime nowUtc)
    {
        if (Baselines == null) return;
        foreach (var m in _metrics.Values)
        {
            if (m.IsGameEndpoint) continue;
            int replies;
            double median;
            lock (m)
            {
                replies = m.RecentHistory.Count(x => x.Success);
                median = m.MedianLatency;
            }
            // only a window with enough replies describes the target; a mostly failing one says nothing about its latency
            if (replies >= MinSamplesForBaseline) Baselines.Add(BaselineMemory.KeyOf(m.Protocol, m.TargetHost), median, nowUtc);
        }
        Baselines.Save();
    }

    /// <summary>Raised once per completed measurement cycle with the cycle time (UTC).</summary>
    public event EventHandler<DateTime>? CycleCompleted;

    public MeasurementEngine(
        NetworkDiscoveryService discoveryService,
        PingService pingService,
        TcpProbeService tcpService,
        HttpProbeService httpService,
        DnsProbeService dnsService,
        DiagnosticEngine diagnosticEngine,
        EventLogService eventLogService)
    {
        _discoveryService = discoveryService;
        _pingService = pingService;
        _tcpService = tcpService;
        _httpService = httpService;
        _dnsService = dnsService;
        _diagnosticEngine = diagnosticEngine;
        _eventLogService = eventLogService;

        _discoveryService.NetworkChanged += (s, change) =>
        {
            UpdateGatewayTarget(change.Info);
            _eventLogService.LogEvent(change.Roamed ? "WifiRoam" : "NetworkChange", change.Severity, change.Description, change.Info.Name);
        };
        _discoveryService.InterfaceRefreshed += (s, nic) =>
        {
            CurrentNic = nic;
            NetworkInterfaceUpdated?.Invoke(this, nic);
        };
    }

    public void Start()
    {
        if (IsRunning) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();

        // Initial discovery
        CurrentNic = _discoveryService.GetActiveInterfaceInfo();
        UpdateGatewayTarget(CurrentNic);
        NetworkInterfaceUpdated?.Invoke(this, CurrentNic);

        _monitoringTask = Task.Run(() => MonitoringLoopAsync(_cts.Token));
        _discoveryService.StartPolling(TimeSpan.FromSeconds(20));
    }

    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        _discoveryService.StopPolling();
        _cts?.Cancel();
        try
        {
            _monitoringTask?.Wait(2000);
        }
        catch { }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Targets added at runtime (endpoints observed on a game process). Not persisted in settings.</summary>
    public IReadOnlyList<TargetConfig> DynamicTargets => _dynamicTargets.Values.ToList();

    public bool AddDynamicTarget(TargetConfig target) => _dynamicTargets.TryAdd(target.Id, target);

    public bool RemoveDynamicTarget(string targetId)
    {
        if (!_dynamicTargets.TryRemove(targetId, out _)) return false;

        lock (_ingestLock)
        {
            _metrics.TryRemove(targetId, out _);
            _previousLatencies.TryRemove(targetId, out _);
            _windows.Remove(targetId);
            _consecutiveFailures.Remove(targetId);
            _firstFailureUtc.Remove(targetId);
            foreach (var prefix in new[] { "outage|", "loss|", "endpoint|" }) _episodes.Remove(prefix + targetId);
        }
        return true;
    }

    public void ClearDynamicTargets()
    {
        foreach (var id in _dynamicTargets.Keys.ToList()) RemoveDynamicTarget(id);
    }

    /// <summary>Forgets statistics of targets that were removed in Settings so they stop appearing in the UI. Call after changing Settings.Targets.</summary>
    public void SyncTargets()
    {
        lock (_ingestLock)
        {
            var keep = Settings.Targets.Select(t => t.Id).Concat(_dynamicTargets.Keys).ToHashSet();
            foreach (var id in _metrics.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _metrics.TryRemove(id, out _);
                _previousLatencies.TryRemove(id, out _);
                _windows.Remove(id);
                _consecutiveFailures.Remove(id);
                _firstFailureUtc.Remove(id);
                foreach (var prefix in new[] { "outage|", "loss|", "endpoint|" }) _episodes.Remove(prefix + id);
            }
        }

        MetricsUpdated?.Invoke(this, _metrics.Values.ToList());
    }

    private TargetConfig? FindConfig(string id) =>
        Settings.Targets.FirstOrDefault(t => t.Id == id) ?? (_dynamicTargets.TryGetValue(id, out var d) ? d : null);

    private void UpdateGatewayTarget(NetworkInterfaceInfo nic)
    {
        var gwTarget = Settings.Targets.FirstOrDefault(t => t.IsGateway);
        if (gwTarget != null && !string.IsNullOrEmpty(nic.Ipv4Gateway))
        {
            gwTarget.Host = nic.Ipv4Gateway;
            gwTarget.Name = $"Gateway ({nic.Ipv4Gateway})";
        }
    }

    private async Task MonitoringLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cycleStart = DateTime.UtcNow;

                // 1. Probe all enabled targets asynchronously
                var enabledTargets = Settings.Targets.Where(t => t.IsEnabled).Concat(_dynamicTargets.Values).ToList();
                var probeTasks = enabledTargets.Select(t => ProbeSingleTargetAsync(t, ct)).ToList();

                var measurements = await Task.WhenAll(probeTasks);

                // 2. Process measurements, update windows/events and run the diagnostic engine
                IngestCycle(measurements, cycleStart);

                // 3. Sleep for remaining interval
                int elapsedMs = (int)(DateTime.UtcNow - cycleStart).TotalMilliseconds;
                int delayMs = Math.Max(100, Settings.MonitoringIntervalMs - elapsedMs);
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventLogService.LogEvent("EngineError", "Warning", $"Monitoring error: {ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Processes one full cycle of probe results: per-target statistics, event detection, connectivity tracking,
    /// diagnosis and notifications. Public so synthetic measurements can drive the engine deterministically.
    /// </summary>
    public void IngestCycle(IReadOnlyList<Measurement> measurements, DateTime? cycleUtc = null)
    {
        DateTime cycleTime = cycleUtc ?? (measurements.Count > 0 ? measurements.Max(m => m.Timestamp) : DateTime.UtcNow);

        lock (_ingestLock)
        {
            _eventLogService.DedupWindow = TimeSpan.FromSeconds(Settings.EventDedupSeconds);

            foreach (var m in measurements)
            {
                ProcessMeasurement(m);
            }

            TrackConnectivity(measurements, cycleTime);

            var gwMetrics = _metrics.Values.FirstOrDefault(m => m.IsGateway);
            var dnsMetrics = _metrics.Values.FirstOrDefault(m => m.IsDnsResolver);
            var internetMetrics = _metrics.Values.Where(m => !m.IsGateway && !m.IsDnsResolver && !m.IsGameEndpoint).ToList();

            CurrentDiagnosis = _diagnosticEngine.Analyze(CurrentNic, gwMetrics, internetMetrics, dnsMetrics);
        }

        CycleCompleted?.Invoke(this, cycleTime);
        MetricsUpdated?.Invoke(this, _metrics.Values.ToList());
        DiagnosisUpdated?.Invoke(this, CurrentDiagnosis);
    }

    private async Task<Measurement> ProbeSingleTargetAsync(TargetConfig target, CancellationToken ct)
    {
        if (target.IsGateway && (target.Host == "auto" || string.IsNullOrEmpty(target.Host)))
        {
            if (!string.IsNullOrEmpty(CurrentNic.Ipv4Gateway))
            {
                target.Host = CurrentNic.Ipv4Gateway;
            }
            else
            {
                return new Measurement
                {
                    TargetId = target.Id,
                    TargetName = target.Name,
                    TargetHost = target.Host,
                    Protocol = target.Protocol,
                    Success = false,
                    ErrorMessage = "No default gateway found"
                };
            }
        }

        return target.Protocol switch
        {
            NetworkProtocol.Icmp => await _pingService.ProbeAsync(target, 1500, ct),
            NetworkProtocol.Tcp => await _tcpService.ProbeAsync(target, 2000, ct),
            NetworkProtocol.Https => await _httpService.ProbeAsync(target, 3500, ct),
            NetworkProtocol.Dns => await _dnsService.ProbeAsync(target, 2000, ct),
            _ => await _pingService.ProbeAsync(target, 1500, ct)
        };
    }

    private RollingWindow GetWindow(string targetId)
    {
        var duration = TimeSpan.FromSeconds(Settings.RecentWindowSeconds);
        if (!_windows.TryGetValue(targetId, out var window) || window.Duration != duration)
        {
            window = new RollingWindow(duration);
            _windows[targetId] = window;
        }
        return window;
    }

    private void ProcessMeasurement(Measurement m)
    {
        var cfg = FindConfig(m.TargetId);
        // a probe that finished after its dynamic target was removed must not resurrect it
        if (cfg == null && m.TargetId.StartsWith("game:", StringComparison.Ordinal)) return;
        var metrics = _metrics.GetOrAdd(m.TargetId, id => new TargetMetrics
        {
            TargetId = id,
            TargetName = m.TargetName,
            TargetHost = m.TargetHost,
            Protocol = m.Protocol,
            IsGateway = cfg?.IsGateway ?? false,
            IsDnsResolver = cfg?.IsDnsResolver ?? false,
            Kind = cfg != null ? SessionRecorder.KindOf(cfg) : MeasurementKind.ReferenceEndpoint
        });

        var window = GetWindow(m.TargetId);
        int maxSamples = Math.Max(60, (Settings.HistoryWindowMinutes * 60 * 1000) / Math.Max(250, Settings.MonitoringIntervalMs));

        lock (metrics)
        {
            metrics.TotalProbes++;
            metrics.LastUpdate = m.Timestamp;
            metrics.TargetHost = m.TargetHost;
            window.Add(m.Timestamp, m.Success, m.RttMs);

            if (m.Success)
            {
                _consecutiveFailures[m.TargetId] = 0;
                // _firstFailureUtc is kept until the outage-ended report below has used it
                metrics.CurrentLatency = m.RttMs;
                metrics.MinLatency = Math.Min(metrics.MinLatency, m.RttMs);
                metrics.MaxLatency = Math.Max(metrics.MaxLatency, m.RttMs);
                metrics.IsReachable = true;
                metrics.LastError = null;
                metrics.LastHttpsBreakdown = m.HttpsBreakdown;

                if (_previousLatencies.TryGetValue(m.TargetId, out double prevLatency))
                {
                    metrics.SmoothedJitterRfc3550 = StatisticalCalculator.UpdateRfc3550Jitter(metrics.SmoothedJitterRfc3550, m.RttMs, prevLatency);

                    double jump = m.RttMs - prevLatency;
                    // HTTPS handshakes and DNS lookups include server and cache time that swings by hundreds of ms on its own;
                    // a "spike" there says nothing about the connection, so only plain round trips are reported
                    bool roundTrip = m.Protocol is NetworkProtocol.Icmp or NetworkProtocol.Tcp;
                    if (Settings.NotifyOnLatencySpike && roundTrip && jump >= Settings.LatencySpikeNotifyThreshold)
                    {
                        _eventLogService.LogEvent(
                            "LatencySpike",
                            "Warning",
                            $"Latency to {m.TargetName} spiked from {prevLatency:F1} ms to {m.RttMs:F1} ms (+{jump:F1} ms)",
                            m.TargetName,
                            m.RttMs,
                            timestamp: m.Timestamp);
                    }
                }
                _previousLatencies[m.TargetId] = m.RttMs;
            }
            else
            {
                metrics.FailedProbes++;
                metrics.LastError = m.ErrorMessage;
                int failuresSoFar = _consecutiveFailures.GetValueOrDefault(m.TargetId);
                if (failuresSoFar == 0) _firstFailureUtc[m.TargetId] = m.Timestamp;
                _consecutiveFailures[m.TargetId] = failuresSoFar + 1;
            }

            metrics.RecentHistory.Add(m);
            while (metrics.RecentHistory.Count > maxSamples)
            {
                metrics.RecentHistory.RemoveAt(0);
            }

            if (!m.Success && metrics.RecentHistory.Count > 0 && metrics.RecentHistory.TakeLast(3).All(x => !x.Success))
            {
                metrics.IsReachable = false;
            }

            var rtts = metrics.RecentHistory.Where(x => x.Success).Select(x => x.RttMs).ToList();
            if (rtts.Count > 0)
            {
                metrics.RollingAvg = StatisticalCalculator.CalculateMean(rtts);
                metrics.MedianLatency = StatisticalCalculator.CalculateMedian(rtts);
                metrics.P95Latency = StatisticalCalculator.CalculatePercentile(rtts, 0.95);
                // "usual" comes from the long-term memory once it has learned this target; until then from the recent window
                metrics.BaselineLatencyMs = Baselines?.Usual(BaselineMemory.KeyOf(m.Protocol, m.TargetHost))
                    ?? (rtts.Count >= MinSamplesForBaseline ? metrics.MedianLatency : 0);
            }

            // Recent-window statistics describe current conditions and recover once probes succeed again.
            metrics.PacketLossPercent = window.LossPercent;
            metrics.Jitter = window.JitterMs;
            metrics.RecentSampleCount = window.Count;
            metrics.RecentWindowSeconds = window.Duration.TotalSeconds;
            metrics.SessionPacketLossPercent = StatisticalCalculator.CalculatePacketLoss(metrics.FailedProbes, metrics.TotalProbes);

            DetectTargetEpisodes(metrics, m);
        }

        lock (_historyLock)
        {
            _allHistory.Add(m);
            if (_allHistory.Count > 5000)
            {
                _allHistory.RemoveAt(0);
            }
        }

        MeasurementProcessed?.Invoke(this, m);
    }

    /// <summary>Reports outages and sustained loss once when they begin and once when they clear.</summary>
    private void DetectTargetEpisodes(TargetMetrics metrics, Measurement m)
    {
        var now = m.Timestamp;
        int failures = _consecutiveFailures.GetValueOrDefault(m.TargetId);

        if (metrics.IsGateway || metrics.IsDnsResolver)
        {
            string key = "outage|" + m.TargetId;
            string eventType = metrics.IsGateway ? "GatewayDegraded" : "DnsFailure";
            string label = metrics.IsGateway ? "Gateway" : "DNS resolution";

            var transition = _episodes.Update(key, failures >= OutageFailureThreshold, now, out var duration);
            if (transition == EpisodeTransition.Started)
            {
                _eventLogService.LogEvent(eventType, metrics.IsGateway ? "Critical" : "Warning",
                    $"{label} stopped responding ({failures} consecutive failed probes): {m.ErrorMessage}",
                    m.TargetName, timestamp: now);
            }
            else if (transition == EpisodeTransition.Ended)
            {
                if (_firstFailureUtc.TryGetValue(m.TargetId, out var firstFailure)) duration = now - firstFailure;
                _firstFailureUtc.Remove(m.TargetId);
                _eventLogService.LogEvent("Restored", "Info",
                    $"{label} responding again after about {duration.TotalSeconds:F0} s.",
                    m.TargetName, metrics.CurrentLatency, timestamp: now);
            }
        }

        if (metrics.IsGameEndpoint)
        {
            // An endpoint that never answered is "not responding to this method" (it may filter probes), not an outage.
            // Only an endpoint that previously answered and then goes silent is reported.
            var transition = _episodes.Update("endpoint|" + m.TargetId, metrics.ReplyCount > 0 && failures >= 3, now, out var duration);
            if (transition == EpisodeTransition.Started)
            {
                _eventLogService.LogEvent("GameEndpointUnreachable", "Warning",
                    $"{m.TargetName} stopped answering probes ({failures} in a row) after previously answering. The connection may have ended or changed, or the endpoint may be filtering probes.",
                    m.TargetName, timestamp: now);
            }
            else if (transition == EpisodeTransition.Ended)
            {
                _eventLogService.LogEvent("Restored", "Info", $"{m.TargetName} is answering probes again.", m.TargetName, metrics.CurrentLatency, timestamp: now);
            }
        }

        if (Settings.NotifyOnPacketLoss && !(metrics.IsGameEndpoint && metrics.ReplyCount == 0))
        {
            string key = "loss|" + m.TargetId;
            double threshold = Settings.PacketLossNotifyThreshold;
            bool lossy = metrics.RecentSampleCount >= MinSamplesForLossEvent && metrics.PacketLossPercent >= threshold;
            bool cleared = metrics.PacketLossPercent < threshold / 2.0;
            bool active = _episodes.IsActive(key);

            // hysteresis: stay "lossy" until loss falls below half the threshold
            var transition = _episodes.Update(key, lossy || (active && !cleared), now, out var duration);
            if (transition == EpisodeTransition.Started)
            {
                _eventLogService.LogEvent("PacketLoss", "Warning",
                    $"Observed probe loss to {m.TargetName}: {metrics.PacketLossPercent:F0}% over the last {metrics.RecentWindowSeconds:F0} s ({metrics.RecentSampleCount} probes). This is loss of probes, not necessarily of game traffic.",
                    m.TargetName, metrics.PacketLossPercent, timestamp: now);
            }
            else if (transition == EpisodeTransition.Ended)
            {
                _eventLogService.LogEvent("Restored", "Info",
                    $"Probe loss to {m.TargetName} cleared (now {metrics.PacketLossPercent:F0}% over the last {metrics.RecentWindowSeconds:F0} s) after about {duration.TotalSeconds:F0} s.",
                    m.TargetName, metrics.PacketLossPercent, timestamp: now);
            }
        }
    }

    /// <summary>
    /// Declares the Internet unreachable after several consecutive cycles in which nothing on the internet answered.
    /// The router and the DNS lookup do not count as the internet: a router keeps answering when the modem or the
    /// provider is down (the most common outage), and it can answer DNS lookups from its cache. Internet servers going
    /// silent only counts once one of them has answered since start, so a network that blocks pings is not "down";
    /// nothing answering at all, the router included, always counts.
    /// </summary>
    private void TrackConnectivity(IReadOnlyList<Measurement> cycle, DateTime cycleTime)
    {
        if (cycle.Count == 0) return;

        // endpoints chosen from a game process say nothing about whether the Internet itself is down
        cycle = cycle.Where(m => !m.TargetId.StartsWith("game:", StringComparison.Ordinal)).ToList();
        if (cycle.Count == 0) return;

        bool IsLocal(Measurement m) => FindConfig(m.TargetId) is { } c && (c.IsGateway || c.IsDnsResolver);
        var internet = cycle.Where(m => !IsLocal(m)).ToList();
        bool internetAnswered = internet.Any(m => m.Success);
        bool routerAnswered = cycle.Any(m => m.Success && FindConfig(m.TargetId)?.IsGateway == true);
        if (internetAnswered) _internetSeenUp = true;

        bool down = !cycle.Any(m => m.Success) || (internet.Count > 0 && !internetAnswered && _internetSeenUp);
        if (!down)
        {
            if (_internetDown)
            {
                var since = _firstFailedCycleUtc ?? cycleTime;
                _eventLogService.LogEvent("Restored", "Info",
                    $"Back online: the internet answers again after about {(cycleTime - since).TotalSeconds:F0} s without a reply.",
                    Sessions.SessionRecorder.ConnectivityScope, timestamp: cycleTime);
            }
            _internetDown = false;
            _zeroSuccessCycles = 0;
            _firstFailedCycleUtc = null;
            _routerAnsweredInFailedRun = false;
            return;
        }

        _firstFailedCycleUtc ??= cycleTime;
        _zeroSuccessCycles++;
        _routerAnsweredInFailedRun |= routerAnswered;
        if (_zeroSuccessCycles >= DisconnectCycleThreshold && !_internetDown)
        {
            _internetDown = true;
            bool beyond = _routerAnsweredInFailedRun;
            _eventLogService.LogEvent("Disconnected", "Critical",
                beyond
                    ? $"Nothing on the internet answered for {_zeroSuccessCycles} cycles in a row, while your router did: the break is beyond your home network, at your modem or your provider."
                    : $"Nothing answered for {_zeroSuccessCycles} cycles in a row, not even your router: this PC lost its connection to your network (cable, Wi-Fi or the router itself).",
                Sessions.SessionRecorder.ConnectivityScope, contextData: beyond ? BeyondRouterContext : LocalContext, timestamp: _firstFailedCycleUtc);
        }
    }

    public IReadOnlyList<Measurement> GetAllHistory()
    {
        lock (_historyLock)
        {
            return _allHistory.ToList();
        }
    }

    public IReadOnlyList<TargetMetrics> GetCurrentMetrics()
    {
        return _metrics.Values.ToList();
    }
}
