using System.Collections.Generic;

namespace Lagwise.Core.Models;

public class NetworkInterfaceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty; // Ethernet, Wireless80211, etc.
    public string OperationalStatus { get; set; } = string.Empty; // Up, Down
    public long SpeedBps { get; set; } // Link speed in bits/second
    public string SpeedDisplay { get; set; } = string.Empty; // e.g. "1 Gbps", "1200 Mbps"
    public string Ipv4Address { get; set; } = string.Empty;
    public string Ipv4Subnet { get; set; } = string.Empty;
    public string Ipv4Gateway { get; set; } = string.Empty;
    public string Ipv6Address { get; set; } = string.Empty;
    public string Ipv6Gateway { get; set; } = string.Empty;
    public List<string> DnsServers { get; set; } = new();

    /// <summary>
    /// The VPN or tunnel adapter Windows sends internet traffic through, when that is not the adapter with the router
    /// (or the adapter is a known VPN). Null for a direct connection.
    /// </summary>
    public string? TunnelName { get; set; }

    // Wi-Fi specific information
    public bool IsWifi { get; set; }
    public string? WifiSsid { get; set; }
    public int? WifiSignalPercent { get; set; }
    public string? WifiRadioType { get; set; } // 802.11ax, 802.11ac, etc.
    public int? WifiChannel { get; set; }
    public string? WifiBssid { get; set; }
    public string? WifiReceiveRate { get; set; }
    public string? WifiTransmitRate { get; set; }
    /// <summary>How the Wi-Fi network is protected, as Windows reports it ("WPA2-Personal", "WPA3-Personal", "Open").</summary>
    public string? WifiAuthentication { get; set; }
    /// <summary>The encryption in use ("CCMP" is AES, "GCMP", "TKIP" is old, "None").</summary>
    public string? WifiCipher { get; set; }
    /// <summary>Windows withheld the Wi-Fi details: since Windows 11 24H2 only apps allowed to use location get them.</summary>
    public bool WifiNeedsLocationPermission { get; set; }
}
