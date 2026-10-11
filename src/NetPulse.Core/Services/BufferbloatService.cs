using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NetPulse.Core.Models;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Services;

public class BufferbloatProgress
{
    public string Phase { get; set; } = string.Empty; // "Baseline", "Download", "Upload", "Completed"
    public int Percent { get; set; }
    public double CurrentLatencyMs { get; set; }
    public double CurrentThroughputMbps { get; set; }
    public string StatusText { get; set; } = string.Empty;
}

/// <summary>
/// Measures latency (ICMP) before and during saturating HTTP load.
/// External service: load traffic is generated against the configured <see cref="ITransferProvider"/> (Cloudflare by default).
/// Cancelling via <see cref="CancellationToken"/> throws <see cref="OperationCanceledException"/>.
/// A run that cannot produce trustworthy numbers returns a result with IsValid = false instead of a grade.
/// </summary>
public class BufferbloatService
{
    private const long MinLoadBytes = 200_000;
    private const int PhaseMs = 5000;

    private readonly PingService _pingService;
    private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ITransferProvider _provider;
    private readonly HttpClient _http;

    public BufferbloatService(PingService pingService, ITransferProvider? provider = null, HttpClient? http = null)
    {
        _pingService = pingService;
        _provider = provider ?? new CloudflareTransferProvider();
        _http = http ?? SharedHttp;
    }

    public async Task<BufferbloatResult> RunTestAsync(
        string testHost = "1.1.1.1",
        IProgress<BufferbloatProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new BufferbloatResult { Timestamp = DateTime.UtcNow, Provider = _provider.DisplayName, ReferenceTarget = testHost };
        var pingTarget = new TargetConfig
        {
            Id = "bufferbloat-target",
            Name = "Bufferbloat Target",
            Host = testHost,
            Protocol = NetworkProtocol.Icmp
        };

        var providerError = await _provider.ValidateAsync(_http, ct);
        if (providerError != null)
        {
            return Invalid(result, providerError, "Retry later, or check whether a firewall or proxy blocks the test provider.");
        }

        // Phase 1: unloaded baseline
        progress?.Report(new BufferbloatProgress { Phase = "Baseline", Percent = 10, StatusText = "Measuring unloaded baseline latency..." });

        var baselineSamples = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            var m = await _pingService.ProbeAsync(pingTarget, 1000, ct);
            if (m.Success) baselineSamples.Add(m.RttMs);
            progress?.Report(new BufferbloatProgress
            {
                Phase = "Baseline",
                Percent = 10 + (i * 2),
                CurrentLatencyMs = m.Success ? m.RttMs : 0,
                StatusText = $"Baseline probe {i + 1}/8: {(m.Success ? $"{m.RttMs:F1} ms" : "no reply")}"
            });
            await Task.Delay(100, ct);
        }

        result.BaselineSampleCount = baselineSamples.Count;
        if (baselineSamples.Count < 3)
        {
            return Invalid(result,
                $"Only {baselineSamples.Count}/8 baseline pings to {testHost} were answered, so latency under load cannot be compared. Check your connection (or whether ICMP is blocked) and retry.",
                "Retry once the connection is stable.");
        }
        result.BaselineLatencyMs = StatisticalCalculator.CalculateMedian(baselineSamples);

        // Phase 2: download load
        progress?.Report(new BufferbloatProgress { Phase = "Download", Percent = 30, StatusText = "Saturating download queue while measuring latency..." });
        var (dlSamples, dlLoad) = await MeasureUnderLoadAsync(pingTarget, RunDownloadLoadAsync, "Download", 30, 35, result.BaselineLatencyMs, progress, ct);
        result.BytesDownloadedDuringLoad = dlLoad.Bytes;
        result.DownloadSampleCount = dlSamples.Count;
        if (dlLoad.Bytes < MinLoadBytes || dlSamples.Count < 3)
        {
            return Invalid(result,
                $"The download load did not run properly ({dlLoad.Bytes / 1_000_000.0:F1} MB transferred, {dlSamples.Count} pings answered under load). {dlLoad.LastError ?? $"Check connectivity to {_provider.DisplayName}."}",
                $"Retry. If it keeps failing, a firewall or proxy may be blocking {_provider.DisplayName}.");
        }
        result.DownloadLoadedLatencyMs = StatisticalCalculator.CalculateMedian(dlSamples);
        result.DownloadDeltaMs = Math.Max(0, result.DownloadLoadedLatencyMs - result.BaselineLatencyMs);

        // Phase 3: upload load
        progress?.Report(new BufferbloatProgress { Phase = "Upload", Percent = 65, StatusText = "Saturating upload queue while measuring latency..." });
        var (ulSamples, ulLoad) = await MeasureUnderLoadAsync(pingTarget, RunUploadLoadAsync, "Upload", 65, 30, result.BaselineLatencyMs, progress, ct);
        result.BytesUploadedDuringLoad = ulLoad.Bytes;
        result.UploadSampleCount = ulSamples.Count;
        if (ulLoad.Bytes < MinLoadBytes || ulSamples.Count < 3)
        {
            return Invalid(result,
                $"The upload load did not run properly ({ulLoad.Bytes / 1_000_000.0:F1} MB sent, {ulSamples.Count} pings answered under load). {ulLoad.LastError ?? $"Check connectivity to {_provider.DisplayName}."}",
                $"Retry. If it keeps failing, a firewall or proxy may be blocking {_provider.DisplayName}.");
        }
        result.UploadLoadedLatencyMs = StatisticalCalculator.CalculateMedian(ulSamples);
        result.UploadDeltaMs = Math.Max(0, result.UploadLoadedLatencyMs - result.BaselineLatencyMs);

        // Phase 4: grade
        double maxDelta = Math.Max(result.DownloadDeltaMs, result.UploadDeltaMs);
        result.Grade = BufferbloatResult.CalculateGrade(maxDelta);
        FormatBufferbloatExplanation(result);

        progress?.Report(new BufferbloatProgress
        {
            Phase = "Completed",
            Percent = 100,
            CurrentLatencyMs = result.BaselineLatencyMs,
            StatusText = $"Test complete: Grade {result.Grade} (+{maxDelta:F1} ms latency increase under load)"
        });

        return result;
    }

    private async Task<(List<double> Samples, LoadCounter Load)> MeasureUnderLoadAsync(
        TargetConfig pingTarget,
        Func<LoadCounter, CancellationToken, Task> loadRunner,
        string phase,
        int percentStart,
        int percentSpan,
        double baselineMs,
        IProgress<BufferbloatProgress>? progress,
        CancellationToken userCt)
    {
        var samples = new List<double>();
        var load = new LoadCounter();
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        phaseCts.CancelAfter(PhaseMs);

        var loadTask = loadRunner(load, phaseCts.Token);
        var sw = Stopwatch.StartNew();
        try
        {
            while (!phaseCts.IsCancellationRequested)
            {
                var m = await _pingService.ProbeAsync(pingTarget, 1000, phaseCts.Token);
                if (m.Success)
                {
                    samples.Add(m.RttMs);
                    progress?.Report(new BufferbloatProgress
                    {
                        Phase = phase,
                        Percent = percentStart + Math.Min(percentSpan, (int)(sw.ElapsedMilliseconds * percentSpan / PhaseMs)),
                        CurrentLatencyMs = m.RttMs,
                        StatusText = $"Under {phase.ToLowerInvariant()} load: {m.RttMs:F1} ms (baseline {baselineMs:F1} ms)"
                    });
                }
                await Task.Delay(150, phaseCts.Token);
            }
        }
        catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
        {
            // phase time limit reached
        }

        await loadTask; // load workers swallow their own cancellation
        userCt.ThrowIfCancellationRequested();
        return (samples, load);
    }

    private static BufferbloatResult Invalid(BufferbloatResult r, string reason, string recommendation)
    {
        r.IsValid = false;
        r.Grade = "N/A";
        r.ErrorMessage = reason;
        r.Summary = "Test could not complete.";
        r.Explanation = reason;
        r.Recommendation = recommendation;
        return r;
    }

    private static void FormatBufferbloatExplanation(BufferbloatResult r)
    {
        double maxDelta = Math.Max(r.DownloadDeltaMs, r.UploadDeltaMs);
        if (maxDelta <= 5)
        {
            r.Summary = "Outstanding queue management. Virtually no latency increase detected under heavy load.";
            r.Explanation = "When your connection is saturated with large downloads or uploads, your router handles queues efficiently without causing ping spikes.";
            r.Recommendation = "Your network is ideal for competitive gaming, Discord calls, and 4K streaming simultaneously.";
        }
        else if (maxDelta <= 15)
        {
            r.Summary = $"Great performance. Minimal latency increase under load (+{maxDelta:F0} ms).";
            r.Explanation = "Slight buffering occurs during peak saturation, but it is well within acceptable real-time limits.";
            r.Recommendation = "No configuration changes needed.";
        }
        else if (maxDelta <= 30)
        {
            r.Summary = $"Moderate queue build-up (+{maxDelta:F0} ms increase under load).";
            r.Explanation = "You may notice brief audio stuttering or slight gaming lag spikes if someone on your network initiates a large download/upload.";
            r.Recommendation = "If anyone frequently downloads games or backs up files while you game/stream, enabling SQM (Smart Queue Management) on your router is beneficial.";
        }
        else if (maxDelta <= 60)
        {
            r.Summary = $"Noticeable bufferbloat (+{maxDelta:F0} ms increase under load).";
            r.Explanation = "Router buffers are holding packets too long during bandwidth saturation, introducing noticeable latency.";
            r.Recommendation = "Enable Smart Queue Management (CAKE or fq_codel) on your router to prioritize interactive traffic over bulk transfers.";
        }
        else
        {
            r.Summary = $"Severe bufferbloat detected (+{maxDelta:F0} ms latency increase under load).";
            r.Explanation = "Your router's packet buffer grows excessively large during downloads/uploads, causing dramatic lag spikes, voice call dropouts, and stuttering video.";
            r.Recommendation = "Enabling SQM (CAKE / fq_codel) or QoS on your home router is strongly recommended to maintain low latency during network activity.";
        }
    }

    private sealed class LoadCounter
    {
        private long _bytes;
        private string? _lastError;
        public long Bytes => Interlocked.Read(ref _bytes);
        public string? LastError => Volatile.Read(ref _lastError);
        public void Add(long n) => Interlocked.Add(ref _bytes, n);
        public void Fail(Exception ex) => Volatile.Write(ref _lastError, ex.GetBaseException().Message);
    }

    private async Task RunDownloadLoadAsync(LoadCounter counter, CancellationToken ct)
    {
        var tasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            var buffer = new byte[81920];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var resp = await _http.GetAsync(_provider.DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0) counter.Add(read);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { counter.Fail(ex); }
        });

        await Task.WhenAll(tasks);
    }

    private async Task RunUploadLoadAsync(LoadCounter counter, CancellationToken ct)
    {
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var content = new ByteArrayContent(payload);
                    using var resp = await _http.PostAsync(_provider.UploadUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    counter.Add(payload.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { counter.Fail(ex); }
        });

        await Task.WhenAll(tasks);
    }
}
