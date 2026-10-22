using System;
using System.Collections.Generic;
using System.Linq;
using Lagwise.Core.Models;

namespace Lagwise.Core.Services;

/// <summary>A meaningful change of the active network connection, described in plain words.</summary>
public sealed record NetworkChange(NetworkInterfaceInfo Info, string Description, string Severity, bool Disconnected, bool Roamed);

/// <summary>
/// Compares two snapshots of the active connection and says what actually changed.
/// Windows raises address-change notifications in bursts and for changes nobody cares about (IPv6 privacy addresses,
/// other adapters); only a different adapter, connection type, IPv4 address, router, DNS servers, Wi-Fi network or
/// Wi-Fi access point counts. Signal strength and link rate fluctuate constantly and never count.
/// </summary>
public static class NetworkChangeDescriber
{
    public static bool IsConnected(NetworkInterfaceInfo? nic) =>
        nic != null && nic.OperationalStatus == "Up" && !string.IsNullOrEmpty(nic.Ipv4Address);

    /// <summary>Returns null when nothing meaningful changed.</summary>
    public static NetworkChange? Describe(NetworkInterfaceInfo? before, NetworkInterfaceInfo after)
    {
        bool wasUp = IsConnected(before), isUp = IsConnected(after);
        if (!wasUp && !isUp) return null;
        if (wasUp && !isUp)
            return new NetworkChange(after, "The network connection went away: no active adapter with an IPv4 address.", "Critical", true, false);
        if (!wasUp)
            return new NetworkChange(after, $"Connected via {Label(after)}{AddressText(after)}.", "Info", false, false);

        var b = before!;
        if (!string.Equals(b.Id, after.Id, StringComparison.OrdinalIgnoreCase) || !string.Equals(b.InterfaceType, after.InterfaceType, StringComparison.OrdinalIgnoreCase))
        {
            string what = Kind(b) == Kind(after) ? $"Switched adapter: {b.Name} to {after.Name}" : $"Switched from {Kind(b)} to {Kind(after)}";
            return new NetworkChange(after, $"{what} ({Label(after)}{AddressText(after)}). Expect a short interruption; ongoing game connections usually drop.", "Warning", false, false);
        }

        if (after.IsWifi && !string.IsNullOrEmpty(after.WifiSsid) && !string.Equals(b.WifiSsid, after.WifiSsid, StringComparison.Ordinal) && !string.IsNullOrEmpty(b.WifiSsid))
            return new NetworkChange(after, $"Joined a different Wi-Fi network: \"{b.WifiSsid}\" to \"{after.WifiSsid}\".", "Warning", false, false);

        var parts = new List<string>();
        if (b.Ipv4Address != after.Ipv4Address) parts.Add($"IP address {Or(b.Ipv4Address)} to {Or(after.Ipv4Address)}");
        if (b.Ipv4Gateway != after.Ipv4Gateway) parts.Add($"router {Or(b.Ipv4Gateway)} to {Or(after.Ipv4Gateway)}");
        if (!b.DnsServers.SequenceEqual(after.DnsServers, StringComparer.OrdinalIgnoreCase))
            parts.Add($"DNS servers {List(b.DnsServers)} to {List(after.DnsServers)}");
        if (parts.Count > 0)
            return new NetworkChange(after, $"Network settings changed on {after.Name}: {string.Join("; ", parts)}.", parts.Count == 1 && parts[0].StartsWith("DNS", StringComparison.Ordinal) ? "Info" : "Warning", false, false);

        if (after.IsWifi && !string.IsNullOrEmpty(b.WifiBssid) && !string.IsNullOrEmpty(after.WifiBssid) && !string.Equals(b.WifiBssid, after.WifiBssid, StringComparison.OrdinalIgnoreCase))
            return new NetworkChange(after,
                $"Wi-Fi moved to a different access point on \"{after.WifiSsid}\" ({b.WifiBssid} to {after.WifiBssid}{(after.WifiSignalPercent is int s ? $", signal {s}%" : "")}). Roaming can cause a brief lag spike.",
                "Warning", false, true);

        return null;
    }

    private static string Kind(NetworkInterfaceInfo n) => n.IsWifi ? "Wi-Fi" : n.InterfaceType switch
    {
        "Ethernet" or "GigabitEthernet" or "FastEthernetT" or "FastEthernetFx" => "wired Ethernet",
        "Wireless80211" => "Wi-Fi",
        "Ppp" => "a dial-up/PPP link",
        "Wwanpp" or "Wwanpp2" => "mobile broadband",
        _ => n.InterfaceType
    };

    private static string Label(NetworkInterfaceInfo n) =>
        n.IsWifi && !string.IsNullOrEmpty(n.WifiSsid) ? $"Wi-Fi \"{n.WifiSsid}\"" : $"{n.Name} ({Kind(n)})";

    private static string AddressText(NetworkInterfaceInfo n) => string.IsNullOrEmpty(n.Ipv4Address) ? "" : $", IP {n.Ipv4Address}";
    private static string Or(string s) => string.IsNullOrEmpty(s) ? "none" : s;
    private static string List(List<string> l) => l.Count == 0 ? "none" : string.Join(", ", l);
}
