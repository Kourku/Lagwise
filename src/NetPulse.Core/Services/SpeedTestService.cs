using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NetPulse.Core.Models;

namespace NetPulse.Core.Services;

public class SpeedTestProgress
{
    public string Phase { get; set; } = string.Empty; // "Download", "Upload", "Completed"
    public int Percent { get; set; }
    public double CurrentSpeedMbps { get; set; }
    public long BytesTransferred { get; set; }
    public string StatusText { get; set; } = string.Empty;
}

/// <summary>
/// Multi-stream throughput test against speed.cloudflare.com (external service: Cloudflare).
/// Cancelling via the token throws <see cref="OperationCanceledException"/>; any other failure is reported
/// through <see cref="SpeedTestResult.ErrorMessage"/> rather than as a 0 Mbps "success".
/// </summary>
public class SpeedTestService
{
    private const int PhaseMs = 5000;


    private readonly HttpClient _http;
    private readonly ITransferProvider _provider;

    public SpeedTestService(ITransferProvider? provider = null, HttpClient? http = null)
    {
        _provider = provider ?? new CloudflareTransferProvider();
        _http = http ?? SharedHttp;
    }

    private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    public ITransferProvider Provider => _provider;

    private sealed class Counter
    {
        public long Bytes;
        public string? LastError;
    }

    public async Task<SpeedTestResult> RunSpeedTestAsync(
        IProgress<SpeedTestProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new SpeedTestResult { Timestamp = DateTime.UtcNow, Provider = _provider.DisplayName };
        var totalSw = Stopwatch.StartNew();

        try
        {
            progress?.Report(new SpeedTestProgress { Phase = "Download", Percent = 10, StatusText = $"Checking {_provider.DisplayName}..." });
            var providerError = await _provider.ValidateAsync(_http, ct);
            if (providerError != null)
            {
                result.ErrorMessage = providerError;
                return result;
            }
            var (dlBytes, dlSec, dlErr) = await RunPhaseAsync("Download", 10, 40, 4, DownloadWorkerAsync, progress, ct);
            if (dlBytes == 0)
            {
                result.ErrorMessage = $"No download data was received. {dlErr ?? "Check connectivity to speed.cloudflare.com."}";
                return result;
            }
            result.BytesDownloaded = dlBytes;
            result.DownloadSpeedMbps = dlBytes * 8.0 / (dlSec * 1_000_000.0);

            progress?.Report(new SpeedTestProgress { Phase = "Upload", Percent = 55, StatusText = "Preparing upload test..." });
            var (ulBytes, ulSec, ulErr) = await RunPhaseAsync("Upload", 55, 40, 3, UploadWorkerAsync, progress, ct);
            if (ulBytes == 0)
            {
                result.ErrorMessage = $"Download completed ({result.DownloadSpeedMbps:F1} Mbps) but no upload data was accepted. {ulErr ?? string.Empty}".Trim();
                return result;
            }
            result.BytesUploaded = ulBytes;
            result.UploadSpeedMbps = ulBytes * 8.0 / (ulSec * 1_000_000.0);

            result.DurationSeconds = totalSw.Elapsed.TotalSeconds;
            result.CompletedSuccessfully = true;

            progress?.Report(new SpeedTestProgress
            {
                Phase = "Completed",
                Percent = 100,
                CurrentSpeedMbps = result.DownloadSpeedMbps,
                StatusText = $"Speed test finished: {result.DownloadSpeedMbps:F1} Mbps down | {result.UploadSpeedMbps:F1} Mbps up"
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.ErrorMessage = "The test timed out.";
        }
        catch (OperationCanceledException)
        {
            throw; // user cancellation propagates to the caller
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.GetBaseException().Message;
        }

        return result;
    }

    private async Task<(long Bytes, double Seconds, string? Error)> RunPhaseAsync(
        string phase, int percentStart, int percentSpan, int streams,
        Func<Counter, CancellationToken, Task> worker,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken userCt)
    {
        var counter = new Counter();
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        phaseCts.CancelAfter(PhaseMs);

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, streams).Select(_ => worker(counter, phaseCts.Token)).ToList();
        var all = Task.WhenAll(tasks);

        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(200, CancellationToken.None));
            userCt.ThrowIfCancellationRequested();
            long bytes = Interlocked.Read(ref counter.Bytes);
            double mbps = bytes * 8.0 / (Math.Max(0.1, sw.Elapsed.TotalSeconds) * 1_000_000.0);
            progress?.Report(new SpeedTestProgress
            {
                Phase = phase,
                Percent = percentStart + Math.Min(percentSpan, (int)(sw.ElapsedMilliseconds * percentSpan / PhaseMs)),
                CurrentSpeedMbps = mbps,
                BytesTransferred = bytes,
                StatusText = $"Testing {phase.ToLowerInvariant()}: {mbps:F1} Mbps ({bytes / 1_000_000.0:F1} MB)"
            });
        }

        await all; // workers handle their own errors
        sw.Stop();
        userCt.ThrowIfCancellationRequested();
        return (Interlocked.Read(ref counter.Bytes), Math.Max(0.1, sw.Elapsed.TotalSeconds), counter.LastError);
    }

    private async Task DownloadWorkerAsync(Counter counter, CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var resp = await _http.GetAsync(_provider.DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    Interlocked.Add(ref counter.Bytes, read);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { counter.LastError = ex.GetBaseException().Message; }
    }

    private async Task UploadWorkerAsync(Counter counter, CancellationToken ct)
    {
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var content = new ByteArrayContent(payload);
                using var resp = await _http.PostAsync(_provider.UploadUri, content, ct);
                resp.EnsureSuccessStatusCode();
                Interlocked.Add(ref counter.Bytes, payload.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { counter.LastError = ex.GetBaseException().Message; }
    }
}
