using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using NetPulse.Core.Models;

namespace NetPulse.Core.Services;

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
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            // Find the interface with a valid IPv4 default gateway
            NetworkInterface? activeNic = null;
            IPAddress? activeGateway = null;

            foreach (var nic in interfaces)
            {
                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                         !g.Address.Equals(IPAddress.Any) &&
                                         !g.Address.Equals(IPAddress.None));

                if (gw != null)
                {
                    activeNic = nic;
                    activeGateway = gw.Address;
                    break;
                }
            }

            // Fallback to first up interface if no gateway found
            if (activeNic == null && interfaces.Count > 0)
            {
                activeNic = interfaces[0];
            }

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

                // If Wi-Fi, query netsh wlan show interfaces
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
        // full path, and stopped after a few seconds: a hung or stopped WLAN service must never block the readings
        string? output = NetPulse.Core.SystemTools.Capture(NetPulse.Core.SystemTools.Netsh, "wlan show interfaces", TimeSpan.FromSeconds(4));
        if (output != null) ParseWlanInterfaces(output, info);
    }

    /// <summary>Reads the fields NetPulse shows from <c>netsh wlan show interfaces</c> (English labels; security also in a few other languages).</summary>
    public static void ParseWlanInterfaces(string output, NetworkInterfaceInfo info)
    {
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
