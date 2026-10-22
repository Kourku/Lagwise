using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Lagwise.Core.Sites;

public enum DnsOutcome { Ok, NotFound, NoAnswer, Failed }

/// <summary>A name lookup: through Windows (your provider's or router's DNS) or through public DNS over HTTPS.</summary>
public sealed record DnsAnswer(DnsOutcome Outcome, IReadOnlyList<IPAddress> Addresses, double? Ms, string? Error = null, string? Resolver = null)
{
    public static DnsAnswer Failed(string error, string? resolver = null) => new(DnsOutcome.Failed, Array.Empty<IPAddress>(), null, error, resolver);

    /// <summary>Every address is one that leads nowhere on the internet (0.0.0.0, 127.x, private ranges): the usual DNS block.</summary>
    public bool IsDeadEnd => Outcome == DnsOutcome.Ok && Addresses.Count > 0 && Addresses.All(SiteDiagnosis.IsDeadEndAddress);
    public bool HasUsableAddress => Outcome == DnsOutcome.Ok && Addresses.Any(a => !SiteDiagnosis.IsDeadEndAddress(a));
}

public enum TraceStage { Connect, Handshake, Answer, Done }
public enum TraceFailure { None, Timeout, Refused, Reset, Unreachable, CertificateInvalid, TlsError, BadAnswer }

/// <summary>One visit as a browser makes it: connect, secure handshake, ask for the home page. Stops at the first step that fails.</summary>
public sealed record ConnectionTrace(
    IPAddress Address,
    TraceStage FailedAt,
    TraceFailure Failure,
    double? ConnectMs = null,
    double? HandshakeMs = null,
    double? AnswerMs = null,
    int? StatusCode = null,
    string? Location = null,
    bool Challenge = false,
    string? CertificateSubject = null,
    string? CertificateIssuer = null,
    string? Error = null,
    string? IconHref = null)
{
    public bool Answered => FailedAt == TraceStage.Done && StatusCode.HasValue;
}

/// <summary>A site's official status page: "none" / "minor" / "major" / "critical" / "maintenance", and its own words.</summary>
public sealed record StatusPageAnswer(string Indicator, string Description)
{
    public bool ReportsOutage => Indicator is "major" or "critical";
    public bool ReportsTrouble => Indicator is "minor" or "major" or "critical";
}

/// <summary>Everything measured for one site.</summary>
public sealed class SiteEvidence
{
    /// <summary>A line of this PC's hosts file naming the site ("0.0.0.0 example.com"), or null.</summary>
    public string? HostsEntry { get; init; }
    public IPAddress? HostsAddress { get; init; }
    public DnsAnswer SystemDns { get; init; } = DnsAnswer.Failed("not checked");
    public DnsAnswer PublicDns { get; init; } = DnsAnswer.Failed("not checked");
    /// <summary>The visit to the address your DNS gave (or the public one when yours gave none).</summary>
    public ConnectionTrace? Trace { get; init; }
    /// <summary>True when <see cref="Trace"/> used the public DNS address because your DNS gave no usable one.</summary>
    public bool TraceUsedPublicAddress { get; init; }
    /// <summary>A second visit to the public DNS address, made when the first visit failed at an address your DNS gave.</summary>
    public ConnectionTrace? PublicTrace { get; init; }
    /// <summary>When the handshake was cut: did the same server answer a handshake naming another site? Null when not tried.</summary>
    public bool? OtherNameAnswered { get; init; }
    public StatusPageAnswer? Status { get; init; }
    public string? StatusError { get; init; }
}

public enum SiteVerdict
{
    Up,
    Slow,
    BlockedOnThisPc,
    DnsBlocked,
    ProviderBlocked,
    Intercepted,
    LegalBlock,
    SiteProblem,
    Unreachable,
    NoSuchSite,
    Offline
}

public enum StepOutcome { Ok, Warning, Failed, Skipped }

/// <summary>One line of the step-by-step explanation.</summary>
public sealed record SiteStep(string Title, StepOutcome Outcome, string Detail);

public sealed record SiteResult(
    SiteDefinition Site,
    SiteVerdict Verdict,
    string Headline,
    string Explanation,
    string Advice,
    IReadOnlyList<SiteStep> Steps,
    DateTime CheckedUtc,
    double? AnswerMs = null)
{
    /// <summary>Where the page says its icon is, when it answered (used to show the site's own icon).</summary>
    public string? IconHref { get; init; }

    public bool IsReachable => Verdict is SiteVerdict.Up or SiteVerdict.Slow;
    public bool IsBlocked => Verdict is SiteVerdict.BlockedOnThisPc or SiteVerdict.DnsBlocked or SiteVerdict.ProviderBlocked or SiteVerdict.Intercepted or SiteVerdict.LegalBlock;
}

/// <summary>
/// Turns what was measured for a site into a verdict in plain words: up, slow, blocked on this PC, blocked by your DNS,
/// blocked by your provider, intercepted, blocked by a legal order, a problem at the site, unreachable, or no internet.
/// From one PC "down" and "blocked by address" look the same; the verdict says so instead of guessing, and the site's
/// own status page, where it has one, tells them apart.
/// </summary>
public static class SiteDiagnosis
{
    /// <summary>An answer slower than this counts as slow.</summary>
    public const double SlowAnswerMs = 3000;

    public static bool IsDeadEndAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6Loopback) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }

    /// <param name="othersReachable">Whether at least one other site answered in the same check (false: the connection itself looks down; null: unknown).</param>
    /// <param name="internetDown">Lagwise's own monitoring says the internet is down.</param>
    public static SiteResult Diagnose(SiteDefinition site, SiteEvidence e, DateTime checkedUtc, bool? othersReachable = null, bool internetDown = false) =>
        DiagnoseCore(site, e, checkedUtc, othersReachable, internetDown) with { IconHref = e.Trace?.IconHref ?? e.PublicTrace?.IconHref };

    private static SiteResult DiagnoseCore(SiteDefinition site, SiteEvidence e, DateTime checkedUtc, bool? othersReachable, bool internetDown)
    {
        var steps = new List<SiteStep>();
        string name = site.Name;

        SiteResult Result(SiteVerdict v, string explanation, double? answerMs = null) =>
            new(site, v, Headline(v), explanation, Advice(v), steps, checkedUtc, answerMs);

        if (internetDown)
        {
            steps.Add(new("Your connection", StepOutcome.Failed, "Lagwise's own monitoring says the internet is not answering right now."));
            return Result(SiteVerdict.Offline, "Your internet connection is down, so no site can be reached. The site itself may be fine.");
        }

        // 1. this PC
        if (e.HostsEntry != null)
        {
            bool deadEnd = e.HostsAddress != null && IsDeadEndAddress(e.HostsAddress);
            steps.Add(new("This PC", deadEnd ? StepOutcome.Failed : StepOutcome.Warning, $"The hosts file on this PC has the line \"{e.HostsEntry}\"."));
            if (deadEnd)
                return Result(SiteVerdict.BlockedOnThisPc, $"This PC's hosts file sends {site.Host} to {e.HostsAddress}, an address that leads nowhere. Something on this PC blocks it: you, an ad blocker or parental control software. Your provider is not involved.");
        }

        // 2. the name lookups
        steps.Add(SystemDnsStep(e.SystemDns));
        steps.Add(PublicDnsStep(e.SystemDns, e.PublicDns));

        if (e.SystemDns.Outcome == DnsOutcome.NotFound && e.PublicDns.Outcome == DnsOutcome.NotFound)
            return Result(SiteVerdict.NoSuchSite, $"Neither your DNS nor public DNS knows the name {site.Host}. Check the spelling; the site may also have closed.");

        // 3. the visit(s)
        var t = e.Trace;
        if (t != null) steps.AddRange(TraceSteps(t, site.Host, e.TraceUsedPublicAddress ? "public DNS" : "your DNS"));
        if (e.OtherNameAnswered is bool other)
            steps.Add(new("Control handshake", other ? StepOutcome.Warning : StepOutcome.Failed,
                other ? "The same server completed a handshake that named another site: only connections naming this site are cut."
                      : "The same server did not answer a handshake naming another site either."));
        if (e.PublicTrace != null)
            steps.Add(new("Via the public DNS address", e.PublicTrace.Answered ? StepOutcome.Ok : StepOutcome.Failed,
                e.PublicTrace.Answered ? $"The address public DNS gave ({e.PublicTrace.Address}) works: HTTP {e.PublicTrace.StatusCode} after {Ms(e.PublicTrace.AnswerMs)}."
                                       : $"The address public DNS gave ({e.PublicTrace.Address}) fails too: {FailureText(e.PublicTrace, site.Host)}"));

        // 4. the site's own word
        if (site.StatusApi != null)
            steps.Add(e.Status != null
                ? new("Status page", e.Status.ReportsTrouble ? StepOutcome.Warning : StepOutcome.Ok, $"{name}'s own status page says: {e.Status.Description}.")
                : new("Status page", StepOutcome.Skipped, $"{name}'s status page could not be read{(e.StatusError != null ? $" ({e.StatusError})" : "")}."));

        bool systemBad = !e.SystemDns.HasUsableAddress;
        bool publicOk = e.PublicDns.HasUsableAddress;
        bool viaPublicWorks = e.PublicTrace?.Answered == true || (e.TraceUsedPublicAddress && t?.Answered == true);
        string statusNote = e.Status == null ? "" : e.Status.ReportsOutage ? $" {name}'s status page reports an outage, so it is probably down for everyone." :
            !e.Status.ReportsTrouble ? $" {name}'s status page says everything works, which points at a block between you and the site." : "";

        // your DNS gives nothing usable
        if (systemBad)
        {
            if (publicOk && viaPublicWorks)
                return Result(SiteVerdict.DnsBlocked, (e.SystemDns.IsDeadEnd
                    ? $"Your DNS sends {site.Host} to {e.SystemDns.Addresses[0]}, an address that leads nowhere, while public DNS gives its real address, which works."
                    : $"Your DNS could not look up {site.Host}, while public DNS could, and that address works.")
                    + " So the site is up and only your DNS (your provider's, or your router's) stops it.", (e.TraceUsedPublicAddress ? t : e.PublicTrace)?.AnswerMs);
            if (publicOk && othersReachable != false)
                return Result(SiteVerdict.ProviderBlocked, $"Your DNS does not look up {site.Host}, and its real address (from public DNS) does not answer either, while other sites work. Both the lookup and the connection are stopped: a provider block, or the site is down." + statusNote);
            if (othersReachable == false)
                return Result(SiteVerdict.Offline, "No site could be reached and names cannot be looked up: your connection looks down, not this site.");
            return Result(SiteVerdict.Unreachable, $"Your DNS could not look up {site.Host}, and public DNS could not be reached to compare. A DNS problem or a block; it cannot be told apart from here." + statusNote);
        }

        if (t == null)
            return Result(SiteVerdict.Unreachable, "The site was not visited.");

        // the address your DNS gave was visited
        if (!t.Answered)
        {
            if (viaPublicWorks && e.PublicTrace != null)
                return Result(SiteVerdict.DnsBlocked, $"The address your DNS gives for {site.Host} ({t.Address}) {FailureShort(t)}, but the real address from public DNS ({e.PublicTrace.Address}) works. Your DNS sends you to the wrong server, often a provider's block page.", e.PublicTrace.AnswerMs);

            if (t.Failure == TraceFailure.CertificateInvalid)
                return Result(SiteVerdict.Intercepted, $"Something answered for {site.Host} with a certificate that is not the site's ({CertificateText(t)}). An ISP block page, parental controls, antivirus that scans secure connections, a company network or a hotel or café sign-in page is in the way.");

            if (t.FailedAt == TraceStage.Handshake && t.Failure is TraceFailure.Reset or TraceFailure.Timeout && e.OtherNameAnswered == true)
                return Result(SiteVerdict.ProviderBlocked, $"The connection to {site.Host} is {(t.Failure == TraceFailure.Reset ? "cut" : "dropped")} as soon as the secure handshake names the site, while the same server accepts a handshake naming another site. That is filtering by name (SNI) on the way, usually by the provider. Changing DNS does not help." + statusNote);

            if (othersReachable == false)
                return Result(SiteVerdict.Offline, "No site could be reached: your connection looks down, not this site.");

            if (e.Status?.ReportsOutage == true)
                return Result(SiteVerdict.SiteProblem, $"{name} cannot be reached ({FailureShort(t)}) and its own status page reports an outage ({e.Status.Description}): it is down for everyone, not only you.");

            if (t.FailedAt == TraceStage.Answer)
                return Result(SiteVerdict.Unreachable, $"{site.Host} accepted the connection but did not answer the request ({FailureShort(t)}). It may be overloaded, or the provider slows it down." + statusNote);

            return Result(SiteVerdict.Unreachable, $"{site.Host} {FailureShort(t)} at {t.Address} while other sites work. Either the site is down or your provider blocks its address: from one PC the two look the same." + statusNote);
        }

        // it answered
        int code = t.StatusCode!.Value;
        if (code == 451)
            return Result(SiteVerdict.LegalBlock, $"{site.Host} answered \"451 Unavailable For Legal Reasons\": access is refused because of a legal order, by the site or on its behalf.", t.AnswerMs);
        if (code >= 500 && !t.Challenge)
            return Result(SiteVerdict.SiteProblem, $"{site.Host} answered with a server error (HTTP {code}): the problem is at the site, not on your side." + (e.Status?.ReportsTrouble == true ? $" Its status page agrees: {e.Status.Description}." : ""), t.AnswerMs);

        string answered = t.Challenge ? " It answered with a bot check, which is normal for an automated visit and proves it is reachable." :
            code is 401 or 403 or 429 ? $" It answered HTTP {code}, refusing automated visits, which still proves it is reachable." : "";
        string trouble = e.Status?.ReportsTrouble == true ? $" Its status page reports trouble ({e.Status.Description}), so some features may not work." : "";
        if (t.AnswerMs > SlowAnswerMs)
            return Result(SiteVerdict.Slow, $"{site.Host} answered, but only after {Ms(t.AnswerMs)}. It is reachable, but overloaded or slowed down (throttled) on the way." + answered + trouble, t.AnswerMs);
        return Result(SiteVerdict.Up, $"{site.Host} answered in {Ms(t.AnswerMs)}." + answered + trouble, t.AnswerMs);
    }

    public static string Headline(SiteVerdict v) => v switch
    {
        SiteVerdict.Up => "Up",
        SiteVerdict.Slow => "Slow",
        SiteVerdict.BlockedOnThisPc => "Blocked on this PC",
        SiteVerdict.DnsBlocked => "Blocked by your DNS",
        SiteVerdict.ProviderBlocked => "Blocked on the way",
        SiteVerdict.Intercepted => "Intercepted",
        SiteVerdict.LegalBlock => "Legal block",
        SiteVerdict.SiteProblem => "Problem at the site",
        SiteVerdict.Unreachable => "Unreachable",
        SiteVerdict.NoSuchSite => "Name not found",
        _ => "No internet"
    };

    public static string Advice(SiteVerdict v) => v switch
    {
        SiteVerdict.Slow => "Try again in a few minutes. If it is only slow on this connection, the provider may be slowing it down.",
        SiteVerdict.BlockedOnThisPc => @"Remove the line from C:\Windows\System32\drivers\etc\hosts, or turn the block off in the program that added it.",
        SiteVerdict.DnsBlocked => "The site itself works. A public DNS (such as 1.1.1.1, 8.8.8.8 or 9.9.9.9) set in Windows or on your router, or secure DNS in your browser, looks names up without your provider's filter.",
        SiteVerdict.ProviderBlocked => "Changing DNS will not help: the connection itself is stopped. Only the provider can lift a block; whether other ways around it are allowed depends on where you live.",
        SiteVerdict.Intercepted => "Check your antivirus or parental controls for HTTPS scanning or web filtering. On public Wi-Fi, open any web page to finish signing in.",
        SiteVerdict.LegalBlock => "The refusal is deliberate and legal, by the site or on its behalf; nothing on your side causes it.",
        SiteVerdict.SiteProblem => "Nothing to fix on your side: wait for the site to recover.",
        SiteVerdict.Unreachable => "Ask someone on another provider, or use mobile data, to open it. If it works there, your provider blocks it; if not, the site is down.",
        SiteVerdict.NoSuchSite => "Check the address for typing mistakes.",
        SiteVerdict.Offline => "Check the Overview: your connection itself is down.",
        _ => ""
    };

    private static SiteStep SystemDnsStep(DnsAnswer d) => d.Outcome switch
    {
        DnsOutcome.Ok when d.IsDeadEnd => new("Your DNS", StepOutcome.Failed, $"Points the name at {d.Addresses[0]}, an address that leads nowhere."),
        DnsOutcome.Ok => new("Your DNS", StepOutcome.Ok, $"{Addresses(d)} in {Ms(d.Ms)}."),
        DnsOutcome.NotFound => new("Your DNS", StepOutcome.Failed, "Says the name does not exist."),
        DnsOutcome.NoAnswer => new("Your DNS", StepOutcome.Failed, "Did not answer in time."),
        _ => new("Your DNS", StepOutcome.Failed, $"Failed: {d.Error}.")
    };

    private static SiteStep PublicDnsStep(DnsAnswer system, DnsAnswer d)
    {
        string who = d.Resolver ?? "Public DNS";
        return d.Outcome switch
        {
            DnsOutcome.Ok when d.IsDeadEnd => new("Public DNS", StepOutcome.Warning, $"{who} also gives {d.Addresses[0]}."),
            DnsOutcome.Ok => new("Public DNS", StepOutcome.Ok, $"{who}: {Addresses(d)}" +
                (system.HasUsableAddress && !system.Addresses.Intersect(d.Addresses).Any() ? " (different from yours, which is normal for big sites that answer from servers near you)." : ".")),
            DnsOutcome.NotFound => new("Public DNS", system.Outcome == DnsOutcome.NotFound ? StepOutcome.Failed : StepOutcome.Warning, $"{who} says the name does not exist."),
            _ => new("Public DNS", StepOutcome.Warning, $"Public DNS over HTTPS could not be reached ({d.Error ?? "no answer"}).")
        };
    }

    private static IEnumerable<SiteStep> TraceSteps(ConnectionTrace t, string host, string source)
    {
        if (t.FailedAt == TraceStage.Connect)
        {
            yield return new("Connect", StepOutcome.Failed, $"To {t.Address} (from {source}): {FailureText(t, host)}");
            yield break;
        }
        yield return new("Connect", StepOutcome.Ok, $"To {t.Address} (from {source}) in {Ms(t.ConnectMs)}.");
        if (t.FailedAt == TraceStage.Handshake)
        {
            yield return new("Secure handshake", StepOutcome.Failed, FailureText(t, host));
            yield break;
        }
        yield return new("Secure handshake", StepOutcome.Ok, $"In {Ms(t.HandshakeMs)}, with a valid certificate for {host}.");
        if (!t.Answered)
        {
            yield return new("Answer", StepOutcome.Failed, FailureText(t, host));
            yield break;
        }
        int code = t.StatusCode!.Value;
        string extra = t.Challenge ? " (a bot check)" : t.Location != null && code is >= 300 and < 400 ? $", redirecting to {ShortLocation(t.Location)}" : "";
        var outcome = code == 451 || (code >= 500 && !t.Challenge) ? StepOutcome.Failed : t.AnswerMs > SlowAnswerMs ? StepOutcome.Warning : StepOutcome.Ok;
        yield return new("Answer", outcome, $"HTTP {code}{extra} after {Ms(t.AnswerMs)}.");
    }

    private static string FailureText(ConnectionTrace t, string host) => t.Failure switch
    {
        TraceFailure.Timeout => t.FailedAt switch
        {
            TraceStage.Connect => "no answer (timed out).",
            TraceStage.Handshake => "the handshake got no answer (timed out).",
            _ => "connected, but no answer to the request (timed out)."
        },
        TraceFailure.Refused => "refused the connection.",
        TraceFailure.Reset => t.FailedAt == TraceStage.Handshake ? "the connection was cut (reset) during the handshake." : "the connection was cut (reset).",
        TraceFailure.Unreachable => "no route to that address.",
        TraceFailure.CertificateInvalid => $"the certificate is not valid for {host} ({CertificateText(t)}).",
        TraceFailure.TlsError => $"the handshake failed ({t.Error ?? "TLS error"}).",
        TraceFailure.BadAnswer => $"the answer was not a web page ({t.Error ?? "unreadable"}).",
        _ => t.Error ?? "failed."
    };

    private static string FailureShort(ConnectionTrace t) => t.Failure switch
    {
        TraceFailure.Timeout => "does not answer",
        TraceFailure.Refused => "refuses connections",
        TraceFailure.Reset => "cuts the connection",
        TraceFailure.Unreachable => "has no route",
        TraceFailure.CertificateInvalid => "shows a certificate that is not the site's",
        _ => "fails"
    };

    private static string CertificateText(ConnectionTrace t) =>
        t.CertificateSubject == null ? "no usable certificate" : $"issued to {t.CertificateSubject}{(t.CertificateIssuer != null ? $" by {t.CertificateIssuer}" : "")}";

    private static string ShortLocation(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var u) ? u.Host + (u.AbsolutePath.Length > 1 ? u.AbsolutePath : "") : location;

    private static string Addresses(DnsAnswer d) =>
        d.Addresses.Count == 1 ? d.Addresses[0].ToString() : $"{d.Addresses[0]} and {d.Addresses.Count - 1} more";

    private static string Ms(double? ms) => ms is not double v ? "-" : v >= 1000 ? $"{v / 1000:0.0} s" : $"{v:0} ms";
}
