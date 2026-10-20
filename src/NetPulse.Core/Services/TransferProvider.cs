using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetPulse.Core.Services;

/// <summary>
/// An external HTTP service used to generate download/upload load for the Speed Test and Bufferbloat tests.
/// Using one sends traffic to a third party, so every provider must carry a user-facing disclosure.
/// </summary>
public interface ITransferProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Plain-language statement of what is contacted and when, shown in the UI and exports.</summary>
    string Disclosure { get; }
    Uri DownloadUri { get; }
    Uri UploadUri { get; }

    /// <summary>
    /// Verifies the exact download and upload endpoints are usable before a test starts.
    /// Returns null when healthy, otherwise a human-readable reason.
    /// </summary>
    Task<string?> ValidateAsync(HttpClient http, CancellationToken ct);
}

/// <summary>
/// speed.cloudflare.com. The endpoint is undocumented and size-sensitive: on 2026-10-09 requests for
/// 14 MB and 15 MB returned HTTP 403 while 1 MB, 10 MB and 25 MB returned 200. 25 MB is used and is checked by
/// <see cref="ValidateAsync"/> before every test, so a future change fails loudly instead of producing fake results.
/// </summary>
public sealed class CloudflareTransferProvider : ITransferProvider
{
    public string Id => "cloudflare";
    public string DisplayName => "Cloudflare (speed.cloudflare.com)";
    public string Disclosure =>
        "Speed Test and Bufferbloat download and upload test data from/to speed.cloudflare.com (Cloudflare). " +
        "Nothing is sent to it until you start one of these tests; no personal data or session data is uploaded.";
    public Uri DownloadUri { get; } = new("https://speed.cloudflare.com/__down?bytes=25000000");
    public Uri UploadUri { get; } = new("https://speed.cloudflare.com/__up");

    public async Task<string?> ValidateAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var down = await http.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!down.IsSuccessStatusCode)
                return $"{DisplayName} rejected the download request (HTTP {(int)down.StatusCode} {UntrustedText.Clean(down.ReasonPhrase, 60)}).";

            using var content = new ByteArrayContent(new byte[1024]);
            using var up = await http.PostAsync(UploadUri, content, ct);
            if (!up.IsSuccessStatusCode)
                return $"{DisplayName} rejected the upload request (HTTP {(int)up.StatusCode} {UntrustedText.Clean(up.ReasonPhrase, 60)}).";

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"{DisplayName} did not respond in time.";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {DisplayName}: {ex.Message}";
        }
    }
}

public static class TransferProviders
{
    public static IReadOnlyList<ITransferProvider> All { get; } = new ITransferProvider[] { new CloudflareTransferProvider() };

    public static ITransferProvider Resolve(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
