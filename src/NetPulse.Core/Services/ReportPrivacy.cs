using System;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetPulse.Core.Services;

/// <summary>
/// Leaves addresses that identify a home out of exported reports: addresses on the home network (192.168.x.x,
/// 10.x.x.x, ...), shared provider addresses (100.64.x.x) and every IPv6 address (an IPv6 address usually identifies
/// one household). Public servers such as 1.1.1.1 or the hops of a route trace stay, because they say nothing about
/// you. Used unless Settings asks for addresses in reports (a provider's support may need them).
/// </summary>
public static class ReportPrivacy
{
    public const string Hidden = "[hidden]";

    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.Compiled);
    // candidates only: anything with at least two colons and hex groups; IPAddress.TryParse decides
    private static readonly Regex Ipv6 = new(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%\w+)?(?![0-9A-Fa-f:])", RegexOptions.Compiled);

    public static string HideAddresses(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Ipv6.Replace(text, m =>
        {
            string candidate = m.Value.Split('%')[0];
            // a clock time (20:15:00) has three parts and no "::"; an IPv6 address has "::" or at least four parts
            bool shaped = candidate.Contains("::", StringComparison.Ordinal) || candidate.Split(':').Length >= 4;
            return shaped && IPAddress.TryParse(candidate, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? Hidden : m.Value;
        });
        return Ipv4.Replace(text, m => IPAddress.TryParse(m.Value, out var ip) && IsPersonal(ip) ? Hidden : m.Value);
    }

    /// <summary>Home network, shared provider (CGNAT) and link-local addresses.</summary>
    public static bool IsPersonal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return true;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254);
    }
}
