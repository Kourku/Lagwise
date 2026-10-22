using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Lagwise.Core.Models;

namespace Lagwise.Core.Services;

public class NetworkDiscoveryService
{
    /// <summary>Raised only for a meaningful change (see <see cref="NetworkChangeDescriber"/>), after Windows' burst of notifications settles.</summary>
    public event EventHandler<NetworkChange>? NetworkChanged;
    /// <summary>Raised after every refresh, changed or not (Wi-Fi signal and link rate move all the time).</summary>
    public event EventHandler<NetworkInterfaceInfo>? InterfaceRefreshed;

    /// <summary>How long to wait for Windows' address-change notifications to settle before looking.</summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private NetworkInterfaceInfo? _last;
    private Timer? _settle;
    private Timer? _poll;

    public NetworkDiscoveryService()
    {
        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) => OnNetworkChanged();
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (s, e) => OnNetworkChanged();
        }
        catch
        {
            // Ignore if running in restricted sandbox
        }
    }

    private void OnNetworkChanged()
    {
        // Windows raises several notifications per real change; look once they have settled
        lock (_gate)
        {
            _settle ??= new Timer(_ => Refresh());
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Re-reads the Wi-Fi connection periodically, so access-point roaming and signal changes are noticed (Windows does not announce them).</summary>
    public void StartPolling(TimeSpan interval)
    {
        lock (_gate)
        {
            _poll ??= new Timer(_ => { if (_last?.IsWifi == true) Refresh(); });
            _poll.Change(interval, interval);
        }
    }

    public void StopPolling()
    {
        lock (_gate) _poll?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reads the active connection now and raises <see cref="NetworkChanged"/> when it differs meaningfully from the previous reading.</summary>
    public void Refresh()
    {
        try
        {
            var info = GetActiveInterfaceInfo();
            NetworkInterfaceInfo? before;
            lock (_gate) { before = _last; _last = info; }
            InterfaceRefreshed?.Invoke(this, info);
            var change = before == null ? null : NetworkChangeDescriber.Describe(before, info);
            if (change != null) NetworkChanged?.Invoke(this, change);
        }
        catch
        {
            // suppress background event errors
        }
    }

    public virtual NetworkInterfaceInfo GetActiveInterfaceInfo()
    {
        var result = new NetworkInterfaceInfo
        {
            Name = "Unknown",
            Description = "No active interface found",
            OperationalStatus = "Down",
            InterfaceType = "None"
        };

        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            var interfaces = up.Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();

            // the adapter Windows' routing table picks for the internet (a lookup only, nothing is sent), and the adapters with a router
            int? bestIndex = BestInterfaceIndex();
            var egress = bestIndex is int bi ? up.FirstOrDefault(n => Ipv4Index(n) == bi) : null;
            var withGateway = interfaces.Select(n => (Nic: n, Gateway: Ipv4Gateway(n))).Where(x => x.Gateway != null).Select(x => (x.Nic, x.Gateway!)).ToList();

            var (activeNic, activeGateway) = PickActive(egress, withGateway, n => n.Description);
            // Fallback to first up interface if no gateway found
            if (activeNic == null && interfaces.Count > 0)
            {
                activeNic = interfaces[0];
            }
            result.TunnelName = TunnelFor(egress, activeNic);

            if (activeNic != null)
            {
                result.Id = activeNic.Id;
                result.Name = activeNic.Name;
                result.Description = activeNic.Description;
                result.InterfaceType = activeNic.NetworkInterfaceType.ToString();
                result.OperationalStatus = activeNic.OperationalStatus.ToString();
                result.SpeedBps = activeNic.Speed;
                result.SpeedDisplay = FormatSpeed(activeNic.Speed);
                result.IsWifi = activeNic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                var ipProps = activeNic.GetIPProperties();

                // IPv4 & Subnet
                var ipv4Info = ipProps.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4Info != null)
                {
                    result.Ipv4Address = ipv4Info.Address.ToString();
                    result.Ipv4Subnet = ipv4Info.IPv4Mask?.ToString() ?? "255.255.255.0";
                }

                // IPv6
                var ipv6Info = ipProps.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                         !u.Address.IsIPv6LinkLocal);
                if (ipv6Info != null)
                {
                    result.Ipv6Address = ipv6Info.Address.ToString();
                }

                // Gateway
                if (activeGateway != null)
                {
                    result.Ipv4Gateway = activeGateway.ToString();
                }
                else
                {
                    var gwIpv4 = ipProps.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gwIpv4 != null) result.Ipv4Gateway = gwIpv4.Address.ToString();
                }

                var gwIpv6 = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
                if (gwIpv6 != null) result.Ipv6Gateway = gwIpv6.Address.ToString();

                // DNS
                foreach (var dns in ipProps.DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetwork ||
                        (dns.AddressFamily == AddressFamily.InterNetworkV6 && !dns.IsIPv6LinkLocal))
                    {
                        result.DnsServers.Add(dns.ToString());
                    }
                }

                if (result.IsWifi || result.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                    result.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                {
                    result.IsWifi = true;
                    PopulateWifiDetails(result);
                }
            }
        }
        catch (Exception ex)
        {
            result.Description = $"Error reading interfaces: {ex.Message}";
        }

        lock (_gate) _last ??= result;   // the first reading is the baseline later changes are compared with
        return result;
    }

    private static void PopulateWifiDetails(NetworkInterfaceInfo info)
    {
        // Windows' WLAN API first: numbers rather than text, so it reads the same in every Windows language
        var read = WlanInfo.TryRead(info.Id, info);
        if (read == WlanReadResult.Ok) return;
        if (read == WlanReadResult.NeedsLocationPermission) { info.WifiNeedsLocationPermission = true; return; }

        // full path, and stopped after a few seconds: a hung or stopped WLAN service must never block the readings
        string? output = Lagwise.Core.SystemTools.Capture(Lagwise.Core.SystemTools.Netsh, "wlan show interfaces", TimeSpan.FromSeconds(4));
        if (output != null) ParseWlanInterfaces(output, info);
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint bestIfIndex);

    /// <summary>The index of the adapter Windows would use to reach the internet (1.1.1.1), from its routing table; nothing is sent.</summary>
    private static int? BestInterfaceIndex()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            uint destination = BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 }, 0);   // network byte order
            return GetBestInterface(destination, out uint index) == 0 ? (int)index : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static int? Ipv4Index(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GetIPv4Properties()?.Index; }
        catch (NetworkInformationException) { return null; }   // IPv4 is off on this adapter
    }

    private static IPAddress? Ipv4Gateway(NetworkInterface nic) =>
        nic.GetIPProperties().GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.None));

    /// <summary>
    /// The adapter whose router Lagwise measures: the one Windows routes internet traffic through when it has a router,
    /// otherwise (a VPN without one, or no answer from Windows) an adapter with a router, real ones before virtual ones.
    /// </summary>
    public static (T? Nic, IPAddress? Gateway) PickActive<T>(T? egress, IReadOnlyList<(T Nic, IPAddress Gateway)> withGateway, Func<T, string> describe) where T : class
    {
        foreach (var (nic, gateway) in withGateway)
            if (ReferenceEquals(nic, egress)) return (nic, gateway);
        if (withGateway.Count == 0) return (null, null);
        var pick = withGateway.OrderBy(x => LooksVirtual(describe(x.Nic)) ? 1 : 0).First();
        return (pick.Nic, pick.Gateway);
    }

    /// <summary>
    /// The VPN or tunnel in the way: internet traffic leaves through another adapter than the one with the router, or
    /// through an adapter named like a VPN. Null for a direct connection.
    /// </summary>
    private static string? TunnelFor(NetworkInterface? egress, NetworkInterface? active) =>
        egress == null ? null : TunnelFor(egress.Id, egress.Name, egress.Description, active?.Id);

    public static string? TunnelFor(string egressId, string egressName, string egressDescription, string? activeId)
    {
        bool elsewhere = activeId != null && !string.Equals(egressId, activeId, StringComparison.OrdinalIgnoreCase);
        if (!elsewhere && !LooksLikeVpn(egressDescription) && !LooksLikeVpn(egressName)) return null;
        return string.IsNullOrWhiteSpace(egressDescription) || string.Equals(egressName, egressDescription, StringComparison.OrdinalIgnoreCase)
            ? egressName : $"{egressName} ({egressDescription})";
    }

    private static readonly Regex VpnWords = new(
        @"\b(VPN|TAP|WireGuard|Wintun|OpenVPN|NordLynx|AnyConnect|GlobalProtect|PANGP|Fortinet|FortiClient|ExpressVPN|Surfshark|ProtonVPN|Mullvad|Windscribe|ZeroTier|Tailscale|WARP)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An adapter named like a VPN (TAP, WireGuard, OpenVPN, the big VPN services); virtual switches are not.</summary>
    public static bool LooksLikeVpn(string? text) => !string.IsNullOrEmpty(text) && VpnWords.IsMatch(text);

    /// <summary>A virtual machine or virtual switch adapter, used only when Windows did not say which adapter it routes through.</summary>
    public static bool LooksVirtual(string? description) => !string.IsNullOrEmpty(description) &&
        (description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
         || description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) || LooksLikeVpn(description));

    /// <summary>Reads the fields Lagwise shows from <c>netsh wlan show interfaces</c> (English labels; security also in a few other languages).</summary>
    public static void ParseWlanInterfaces(string output, NetworkInterfaceInfo info)
    {
        // Windows 11 24H2 and later: netsh, too, shares nothing without location permission and points to its Settings page
        if (output.Contains("ms-settings:privacy-location", StringComparison.OrdinalIgnoreCase))
        {
            info.WifiNeedsLocationPermission = true;
            return;
        }

        static string? Field(string text, string labels)
        {
            var m = Regex.Match(text, $@"^\s*(?:{labels})\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        info.WifiSsid = Field(output, "SSID") ?? info.WifiSsid;
        if (Field(output, "Signal") is { } signal && int.TryParse(signal.TrimEnd('%', ' '), out int sig)) info.WifiSignalPercent = sig;
        info.WifiRadioType = Field(output, "Radio type") ?? info.WifiRadioType;
        if (Field(output, "Channel") is { } channel && int.TryParse(channel, out int ch)) info.WifiChannel = ch;
        info.WifiBssid = Field(output, "BSSID") ?? info.WifiBssid;
        info.WifiReceiveRate = Field(output, @"Receive rate \(Mbps\)") ?? info.WifiReceiveRate;
        info.WifiTransmitRate = Field(output, @"Transmit rate \(Mbps\)") ?? info.WifiTransmitRate;
        info.WifiAuthentication = Field(output, "Authentication|Authentification|Authentifizierung|Autenticaci.n|Autenticazione") ?? info.WifiAuthentication;
        info.WifiCipher = Field(output, "Cipher|Chiffrement|Verschl.sselung|Cifrado|Crittografia") ?? info.WifiCipher;
    }

    private static string FormatSpeed(long speedBps)
    {
        if (speedBps <= 0) return "Unknown";
        if (speedBps >= 1_000_000_000)
            return $"{(double)speedBps / 1_000_000_000:0.##} Gbps";
        if (speedBps >= 1_000_000)
            return $"{(double)speedBps / 1_000_000:0.##} Mbps";
        if (speedBps >= 1_000)
            return $"{(double)speedBps / 1_000:0.##} Kbps";
        return $"{speedBps} bps";
    }
}
