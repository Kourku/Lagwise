using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NetPulse.Core.Sites;

/// <summary>The measurements a site check makes. Replaced by fakes in tests.</summary>
public interface ISiteProbes
{
    /// <summary>The hosts-file line naming <paramref name="host"/>, or null.</summary>
    string? HostsEntry(string host, out IPAddress? address);
    Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct);
    Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct);
    Task<ConnectionTrace> VisitAsync(IPAddress address, string host, CancellationToken ct);
    /// <summary>Whether the server at <paramref name="address"/> answers a secure handshake naming another site at all.</summary>
    Task<bool> HandshakeAnswersAsync(IPAddress address, string otherName, CancellationToken ct);
    Task<StatusPageAnswer> StatusAsync(string url, CancellationToken ct);
}

/// <summary>
/// The real measurements: Windows' own name lookup (your provider's or router's DNS), public DNS over HTTPS (Cloudflare
/// 1.1.1.1, then Google 8.8.8.8, reached by address so a broken DNS cannot stop them), and one visit to the site as a
/// browser makes it: TCP connect, TLS handshake, one request for the home page. No account, cookie or key is used,
/// and a bot check is reported as such, never solved: it already proves the site answers.
/// </summary>
public sealed class SiteProbes : ISiteProbes
{
    public static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(8);

    public static string UserAgent => $"NetPulse/{AppInfo.Version} (connectivity check)";

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = TimeSpan.FromSeconds(6),
            MaxResponseContentBufferSize = 1024 * 1024   // a status or DNS answer is a few kilobytes; never hold more than 1 MB
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NetPulse/{AppInfo.Version}");
        return client;
    }

    private (DateTime Read, Dictionary<string, (string Line, IPAddress? Address)> Map)? _hosts;
    private readonly object _hostsLock = new();

    public string? HostsEntry(string host, out IPAddress? address)
    {
        address = null;
        var map = HostsMap();
        if (!map.TryGetValue(host, out var hit)) return null;
        address = hit.Address;
        return hit.Line;
    }

    private Dictionary<string, (string Line, IPAddress? Address)> HostsMap()
    {
        lock (_hostsLock)
        {
            if (_hosts is { } cached && DateTime.UtcNow - cached.Read < TimeSpan.FromMinutes(1)) return cached.Map;
            var map = new Dictionary<string, (string Line, IPAddress? Address)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
                if (File.Exists(path))
                    foreach (var raw in File.ReadLines(path))
                    {
                        string line = raw.Split('#')[0].Trim();
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        IPAddress.TryParse(parts[0], out var ip);
                        foreach (var name in parts.Skip(1)) map.TryAdd(name, ($"{parts[0]} {name}", ip));
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable hosts file: nothing to report
            }
            _hosts = (DateTime.UtcNow, map);
            return map;
        }
    }

    public async Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DnsTimeout);
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, cts.Token);
            var ordered = all.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
            return ordered.Count == 0
                ? new DnsAnswer(DnsOutcome.NotFound, ordered, sw.Elapsed.TotalMilliseconds)
                : new DnsAnswer(DnsOutcome.Ok, ordered, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsAnswer(DnsOutcome.NoAnswer, Array.Empty<IPAddress>(), null, "timed out");
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => new DnsAnswer(DnsOutcome.NotFound, Array.Empty<IPAddress>(), sw.Elapsed.TotalMilliseconds, ex.Message),
                SocketError.TryAgain or SocketError.TimedOut => new DnsAnswer(DnsOutcome.NoAnswer, Array.Empty<IPAddress>(), null, ex.Message),
                _ => DnsAnswer.Failed(ex.Message)
            };
        }
    }

    /// <summary>Cloudflare and Google answer DNS over HTTPS in JSON at their own addresses, without any account or key.</summary>
    private static readonly (string Name, string Url)[] PublicResolvers =
    {
        ("Cloudflare DNS (1.1.1.1)", "https://1.1.1.1/dns-query?type=A&name="),
        ("Google DNS (8.8.8.8)", "https://8.8.8.8/resolve?type=A&name=")
    };

    public async Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var (name, url) in PublicResolvers)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url + Uri.EscapeDataString(host));
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DnsTimeout);
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode) { lastError = $"{name}: HTTP {(int)resp.StatusCode}"; continue; }
                var answer = ParseDnsJson(await resp.Content.ReadAsStringAsync(cts.Token), name, sw.Elapsed.TotalMilliseconds);
                if (answer != null) return answer;
                lastError = $"{name}: unreadable answer";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { lastError = $"{name}: timed out"; }
            catch (HttpRequestException ex) { lastError = $"{name}: {ex.GetBaseException().Message}"; }
        }
        return DnsAnswer.Failed(lastError ?? "no answer");
    }

    /// <summary>Reads the JSON form of DNS (RFC 8427 style, as Cloudflare and Google serve it). Null when it is not that.</summary>
    public static DnsAnswer? ParseDnsJson(string json, string resolver, double ms)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number) return null;
            int code = status.GetInt32();
            if (code == 3) return new DnsAnswer(DnsOutcome.NotFound, Array.Empty<IPAddress>(), ms, null, resolver);
            if (code != 0) return new DnsAnswer(DnsOutcome.Failed, Array.Empty<IPAddress>(), ms, $"DNS error {code}", resolver);
            var list = new List<IPAddress>();
            if (root.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array)
                foreach (var a in answers.EnumerateArray())
                    if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 && a.TryGetProperty("data", out var d) && IPAddress.TryParse(d.GetString(), out var ip))
                        list.Add(ip);
            return new DnsAnswer(list.Count > 0 ? DnsOutcome.Ok : DnsOutcome.NotFound, list, ms, null, resolver);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ConnectionTrace> VisitAsync(IPAddress address, string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        // 1. connect
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new IPEndPoint(address, 443), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(address, TraceStage.Connect, TraceFailure.Timeout); }
        catch (SocketException ex) { return new(address, TraceStage.Connect, Map(ex.SocketErrorCode), Error: ex.Message); }
        double connectMs = sw.Elapsed.TotalMilliseconds;

        // 2. secure handshake, naming the site (SNI); the certificate is judged afterwards, so an interception can be described
        using var stream = new NetworkStream(socket, ownsSocket: false);
        SslPolicyErrors errors = SslPolicyErrors.None;
        X509Certificate2? cert = null;
        using var ssl = new SslStream(stream, false, (_, c, _, e) =>
        {
            errors = e;
            cert = c as X509Certificate2;
            return true;
        });
        var handshakeStart = sw.Elapsed;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(address, TraceStage.Handshake, TraceFailure.Timeout, connectMs); }
        catch (AuthenticationException ex) { return new(address, TraceStage.Handshake, TraceFailure.TlsError, connectMs, Error: ex.GetBaseException().Message); }
        catch (IOException ex) { return new(address, TraceStage.Handshake, CutOrError(ex), connectMs, Error: ex.GetBaseException().Message); }
        double handshakeMs = (sw.Elapsed - handshakeStart).TotalMilliseconds;
        if (errors != SslPolicyErrors.None)
            return new(address, TraceStage.Handshake, TraceFailure.CertificateInvalid, connectMs, handshakeMs,
                CertificateSubject: UntrustedText.Clean(cert?.GetNameInfo(X509NameType.SimpleName, false), 120),
                CertificateIssuer: UntrustedText.Clean(cert?.GetNameInfo(X509NameType.SimpleName, true), 120));

        // 3. one request for the home page; only the start of the answer is read
        string request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: {UserAgent}\r\nAccept: text/html,application/xhtml+xml,*/*;q=0.8\r\n" +
                         "Accept-Language: en\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n";
        var askedAt = sw.Elapsed;
        var buffer = new byte[48 * 1024];
        int read = 0;
        double? firstByteMs = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnswerTimeout);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            while (read < buffer.Length)
            {
                int n = await ssl.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n == 0) break;
                firstByteMs ??= (sw.Elapsed - askedAt).TotalMilliseconds;
                read += n;
                if (ParseAnswer(buffer, read) is { Complete: true }) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (read == 0) return new(address, TraceStage.Answer, TraceFailure.Timeout, connectMs, handshakeMs);
        }
        catch (IOException ex)
        {
            if (read == 0) return new(address, TraceStage.Answer, CutOrError(ex), connectMs, handshakeMs, Error: ex.GetBaseException().Message);
        }

        var parsed = ParseAnswer(buffer, read);
        if (parsed is not { StatusCode: int code })
            return new(address, TraceStage.Answer, TraceFailure.BadAnswer, connectMs, handshakeMs, firstByteMs, Error: read == 0 ? "the connection closed without an answer" : "not an HTTP answer");
        return new(address, TraceStage.Done, TraceFailure.None, connectMs, handshakeMs, firstByteMs, code, parsed.Location, parsed.Challenge, IconHref: parsed.IconHref);
    }

    public async Task<bool> HandshakeAnswersAsync(IPAddress address, string otherName, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout + HandshakeTimeout);
            await socket.ConnectAsync(new IPEndPoint(address, 443), cts.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = otherName, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, cts.Token);
            return true;
        }
        catch (AuthenticationException)
        {
            return true;    // the server answered, with a refusal: it is alive and reachable
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    public async Task<StatusPageAnswer> StatusAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return ParseStatusJson(await resp.Content.ReadAsStringAsync(ct)) ?? throw new HttpRequestException("not a status page answer");
    }

    /// <summary>The Statuspage summary: <c>{"status":{"indicator":"none","description":"All Systems Operational"}}</c>.</summary>
    public static StatusPageAnswer? ParseStatusJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var s)) return null;
            string? indicator = s.TryGetProperty("indicator", out var i) ? i.GetString() : null;
            string? description = s.TryGetProperty("description", out var d) ? d.GetString() : null;
            indicator = UntrustedText.Clean(indicator, 40);
            description = UntrustedText.Clean(description, 160);
            return string.IsNullOrEmpty(indicator) ? null : new StatusPageAnswer(indicator, string.IsNullOrWhiteSpace(description) ? indicator : description);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;    // not the expected shape ("status" a string, say)
        }
    }

    private sealed record Answer(int? StatusCode, string? Location, bool Challenge, bool Complete, string? IconHref = null);

    private static readonly Regex StatusLine = new(@"^HTTP/\d(?:\.\d)?\s+(\d{3})", RegexOptions.Compiled);
    private static readonly string[] ChallengeMarkers = { "just a moment", "cf-chl", "challenge-platform", "attention required", "captcha", "are you a robot", "verify you are human" };
    // a page is untrusted: a time limit keeps a crafted one from keeping the regular expression busy
    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);
    private static readonly Regex IconLink = new(@"<link\b[^>]*\brel\s*=\s*[""'](?:shortcut\s+)?icon[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexLimit);
    private static readonly Regex Href = new(@"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexLimit);

    /// <summary>The icon a page names in its head (<c>&lt;link rel="icon" href="..."&gt;</c>), skipping SVG, which Windows cannot draw.</summary>
    public static string? FindIconHref(string html)
    {
        try
        {
            foreach (Match link in IconLink.Matches(html))
            {
                var href = Href.Match(link.Value);
                if (!href.Success) continue;
                string value = WebUtility.HtmlDecode(href.Groups[1].Value.Trim());
                if (value.Length == 0 || value.Length > 2048 || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Contains(".svg", StringComparison.OrdinalIgnoreCase) || link.Value.Contains("image/svg", StringComparison.OrdinalIgnoreCase)) continue;
                return value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a page built to be slow to read: no icon from it
        }
        return null;
    }

    /// <summary>The status code, the redirect target and whether it is a bot check, from the start of an HTTP/1.1 answer.</summary>
    private static Answer? ParseAnswer(byte[] buffer, int length)
    {
        string text = Encoding.UTF8.GetString(buffer, 0, length);
        int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return length >= buffer.Length ? null : new Answer(null, null, false, false);
        var lines = text[..headerEnd].Split("\r\n");
        var m = StatusLine.Match(lines[0]);
        if (!m.Success) return null;
        int code = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string? Header(string name) => lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).FirstOrDefault();
        string raw = text[(headerEnd + 4)..];
        string body = raw.ToLowerInvariant();
        bool challenge = Header("cf-mitigated")?.Contains("challenge", StringComparison.OrdinalIgnoreCase) == true
                         || (code is 403 or 429 or 503 && ChallengeMarkers.Any(body.Contains));
        // enough to judge: a redirect needs no body; a page needs its head (title and icon), or a few dozen kilobytes
        bool complete = code is >= 300 and < 400 || body.Contains("</head>", StringComparison.Ordinal) || body.Length > 32 * 1024;
        return new Answer(code, UntrustedText.Clean(Header("Location"), 300), challenge, complete, code is >= 200 and < 300 ? FindIconHref(raw) : null);
    }

    private static TraceFailure Map(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => TraceFailure.Refused,
        SocketError.ConnectionReset or SocketError.ConnectionAborted => TraceFailure.Reset,
        SocketError.TimedOut => TraceFailure.Timeout,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => TraceFailure.Unreachable,
        _ => TraceFailure.Reset
    };

    /// <summary>A reset, or the other side closing in the middle (EOF), is the connection being cut.</summary>
    private static TraceFailure CutOrError(IOException ex) =>
        ex.InnerException is SocketException se ? Map(se.SocketErrorCode) : TraceFailure.Reset;
}
