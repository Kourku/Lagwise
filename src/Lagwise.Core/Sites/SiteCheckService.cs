using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Lagwise.Core.Sites;

/// <summary>
/// Checks sites a few at a time and says, for each, whether it is up, slow, or blocked and where. A site is judged
/// twice: as soon as its own measurements are in, and again once the whole check is done, because "nothing answers"
/// means the connection is down when no other site answered either.
/// </summary>
public sealed class SiteCheckService
{
    /// <summary>A neutral name for the control handshake that tells filtering by name (SNI) from a dead server.</summary>
    public const string ControlName = "example.com";

    private readonly ISiteProbes _probes;
    private readonly Func<DateTime> _clock;
    private readonly Func<bool> _internetDown;

    public int Parallelism { get; init; } = 5;

    public SiteCheckService(ISiteProbes? probes = null, Func<DateTime>? clock = null, Func<bool>? internetDown = null)
    {
        _probes = probes ?? new SiteProbes();
        _clock = clock ?? (() => DateTime.UtcNow);
        _internetDown = internetDown ?? (() => false);
    }

    /// <param name="progress">Each site's first verdict, as soon as it is measured (the final ones are returned).</param>
    public async Task<IReadOnlyList<SiteResult>> CheckAsync(IReadOnlyList<SiteDefinition> sites, IProgress<SiteResult>? progress = null, CancellationToken ct = default)
    {
        var evidence = new SiteEvidence[sites.Count];
        var times = new DateTime[sites.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, Parallelism));
        var tasks = sites.Select(async (site, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                try
                {
                    evidence[i] = await GatherAsync(site, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // an unexpected failure (for example IPv6 switched off in Windows) is reported, not thrown
                    evidence[i] = new SiteEvidence { SystemDns = DnsAnswer.Failed(ex.GetBaseException().Message), PublicDns = DnsAnswer.Failed("not checked") };
                }
                times[i] = _clock();
                progress?.Report(SiteDiagnosis.Diagnose(site, evidence[i], times[i], internetDown: _internetDown()));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);

        bool down = _internetDown();
        return sites.Select((site, i) =>
        {
            bool? others = sites.Count < 2 ? null : evidence.Where((_, j) => j != i).Any(Answered);
            return SiteDiagnosis.Diagnose(site, evidence[i], times[i], others, down);
        }).ToList();
    }

    private static bool Answered(SiteEvidence e) => e.Trace?.Answered == true || e.PublicTrace?.Answered == true;

    /// <summary>Everything about one site: hosts file, both name lookups, the visit, the control handshake when needed, the status page.</summary>
    public async Task<SiteEvidence> GatherAsync(SiteDefinition site, CancellationToken ct)
    {
        string? hosts = _probes.HostsEntry(site.Host, out var hostsAddress);
        var statusTask = site.StatusApi == null ? null : StatusAsync(site.StatusApi, ct);
        var systemTask = _probes.SystemDnsAsync(site.Host, ct);
        var publicTask = _probes.PublicDnsAsync(site.Host, ct);
        var system = await systemTask;
        var pub = await publicTask;

        ConnectionTrace? trace = null, publicTrace = null;
        bool usedPublic = false;
        bool? otherName = null;
        bool blockedHere = hosts != null && hostsAddress != null && SiteDiagnosis.IsDeadEndAddress(hostsAddress);
        if (!blockedHere)
        {
            var address = Pick(system);
            if (address == null && (address = Pick(pub)) != null) usedPublic = true;
            if (address != null)
            {
                trace = await _probes.VisitAsync(address, site.Host, ct);
                if (trace.FailedAt == TraceStage.Handshake && trace.Failure is TraceFailure.Reset or TraceFailure.Timeout)
                    otherName = await _probes.HandshakeAnswersAsync(address, ControlName, ct);
                // the address from your DNS failed: does the real one (from public DNS) work?
                var other = Pick(pub);
                if (!usedPublic && !trace.Answered && other != null && !system.Addresses.Contains(other))
                    publicTrace = await _probes.VisitAsync(other, site.Host, ct);
            }
        }

        var (status, statusError) = statusTask == null ? (null, null) : await statusTask;
        return new SiteEvidence
        {
            HostsEntry = hosts,
            HostsAddress = hostsAddress,
            SystemDns = system,
            PublicDns = pub,
            Trace = trace,
            TraceUsedPublicAddress = usedPublic,
            PublicTrace = publicTrace,
            OtherNameAnswered = otherName,
            Status = status,
            StatusError = statusError
        };
    }

    /// <summary>The first address that leads somewhere, IPv4 first (public DNS answers IPv4, and not every home has IPv6).</summary>
    private static IPAddress? Pick(DnsAnswer d) =>
        d.Outcome != DnsOutcome.Ok ? null
        : d.Addresses.Where(a => !SiteDiagnosis.IsDeadEndAddress(a)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();

    private async Task<(StatusPageAnswer?, string?)> StatusAsync(string url, CancellationToken ct)
    {
        try
        {
            return (await _probes.StatusAsync(url, ct), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (null, ex is OperationCanceledException ? "timed out" : ex.GetBaseException().Message);
        }
    }

    // ---- site icons: the site's own favicon, as a browser tab shows it

    /// <summary>
    /// The file holding the site's icon, downloaded from where the page says (<paramref name="iconHref"/>) or from
    /// /favicon.ico, when missing or a month old. Null when the site has none Lagwise can show.
    /// </summary>
    public static async Task<string?> EnsureIconAsync(SiteDefinition site, string folder, CancellationToken ct, string? iconHref = null)
    {
        string stem = Path.Combine(folder, site.Host);
        var existing = new[] { ".ico", ".png", ".gif", ".jpg" }.Select(ext => stem + ext).FirstOrDefault(File.Exists);
        if (existing != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(existing) < TimeSpan.FromDays(30)) return existing;
        // a site without a usable icon is not asked again for a week
        string none = stem + ".none";
        if (existing == null && File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(7)) return null;

        var page = new Uri($"https://{site.Host}/");
        var candidates = new List<Uri>();
        if (iconHref != null && Uri.TryCreate(page, iconHref, out var named) && named.Scheme == Uri.UriSchemeHttps) candidates.Add(named);
        candidates.Add(new Uri(page, "/favicon.ico"));
        foreach (var url in candidates.Distinct())
        {
            var saved = await DownloadIconAsync(url, stem, folder, ct);
            if (saved != null) return saved;
        }
        // last try: the page a redirect leads to (a regional home page) may name its icon
        if (iconHref == null && await IconHrefFromPageAsync(page, ct) is { } found && !candidates.Contains(found))
        {
            var saved = await DownloadIconAsync(found, stem, folder, ct);
            if (saved != null) return saved;
        }
        if (existing == null)
        {
            try { Directory.CreateDirectory(folder); await File.WriteAllTextAsync(none, "", ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return existing;
    }

    private static async Task<Uri?> IconHrefFromPageAsync(Uri page, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await IconHttp.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[64 * 1024];
            int read = 0, n;
            while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), cts.Token)) > 0) read += n;
            string? href = SiteProbes.FindIconHref(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            var final = resp.RequestMessage?.RequestUri ?? page;
            return href != null && Uri.TryCreate(final, href, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async Task<string?> DownloadIconAsync(Uri url, string stem, string folder, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await IconHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxIconBytes) return null;
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(chunk, cts.Token)) > 0)
            {
                if (ms.Length + n > MaxIconBytes) return null;
                ms.Write(chunk, 0, n);
            }
            string? ext = IconExtension(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            if (ext == null) return null;     // a web page in place of an icon, or a format Windows cannot draw (SVG)
            Directory.CreateDirectory(folder);
            foreach (var old in new[] { ".ico", ".png", ".gif", ".jpg" }.Select(e => stem + e).Where(File.Exists)) File.Delete(old);
            await File.WriteAllBytesAsync(stem + ext, ms.ToArray(), cts.Token);
            return stem + ext;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public const int MaxIconBytes = 256 * 1024;

    private static readonly HttpClient IconHttp = CreateIconHttp();
    private static HttpClient CreateIconHttp()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            // a page or a redirect chooses the icon's address: refuse one on this PC or the home network, so a site
            // cannot make Lagwise send requests to the router or other devices
            ConnectCallback = async (context, token) =>
            {
                var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token)).Where(IsPublicAddress).ToArray();
                if (addresses.Length == 0) throw new HttpRequestException("an icon on this PC or the home network is not fetched");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        });
        c.DefaultRequestHeaders.UserAgent.ParseAdd(SiteProbes.UserAgent);
        return c;
    }

    /// <summary>An address on the internet: not this PC, the home network, a provider's shared range, link-local or multicast.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast);
        var b = ip.GetAddressBytes();
        return b[0] != 0 && b[0] < 224 && !Services.ReportPrivacy.IsPersonal(ip);
    }

    /// <summary>Only the image formats Windows draws without help: ICO, PNG, GIF and JPEG, recognised by their first bytes.</summary>
    public static string? IconExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8) return null;
        if (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return ".ico";
        if (b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return ".gif";
        if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
        return null;
    }
}
