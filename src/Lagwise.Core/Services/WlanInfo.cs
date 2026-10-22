using System;
using System.Runtime.InteropServices;
using Lagwise.Core.Models;

namespace Lagwise.Core.Services;

/// <summary>What reading the Wi-Fi connection from Windows' WLAN API gave.</summary>
public enum WlanReadResult
{
    /// <summary>Signal, network name and the rest were read.</summary>
    Ok,
    /// <summary>Windows 11 (24H2 and later) withholds Wi-Fi details from apps that may not use location.</summary>
    NeedsLocationPermission,
    /// <summary>No WLAN service, no such adapter, or not connected: try another way.</summary>
    Unavailable
}

/// <summary>
/// The current Wi-Fi connection from the Native Wi-Fi API (<c>wlanapi.dll</c>): numbers and enums rather than the text
/// of <c>netsh</c>, so it reads the same on Windows in any language. Read-only; nothing is changed or scanned.
/// </summary>
public static class WlanInfo
{
    private const int ErrorAccessDenied = 5;
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    private const int StateConnected = 1;

    /// <summary>Size of WLAN_CONNECTION_ATTRIBUTES, and the offsets Lagwise reads in it (wlanapi.h).</summary>
    public const int ConnectionAttributesSize = 604;
    private const int OffState = 0, OffSsidLength = 520, OffSsid = 524, OffBssid = 560, OffPhyType = 568, OffSignal = 576,
        OffRxRate = 580, OffTxRate = 584, OffAuth = 596, OffCipher = 600;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>Fills the Wi-Fi fields of <paramref name="info"/> for the adapter <paramref name="adapterId"/> (its GUID, as Windows lists adapters).</summary>
    public static WlanReadResult TryRead(string adapterId, NetworkInterfaceInfo info)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(adapterId, out var guid)) return WlanReadResult.Unavailable;
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return WlanReadResult.Unavailable;

            int status = WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out int size, out IntPtr data, out _);
            if (status == ErrorAccessDenied) return WlanReadResult.NeedsLocationPermission;
            if (status != 0 || data == IntPtr.Zero) return WlanReadResult.Unavailable;
            try
            {
                if (size < ConnectionAttributesSize) return WlanReadResult.Unavailable;
                var bytes = new byte[ConnectionAttributesSize];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (!ReadConnection(bytes, info)) return WlanReadResult.Unavailable;
            }
            finally
            {
                WlanFreeMemory(data);
            }

            if (WlanQueryInterface(handle, ref guid, OpcodeChannelNumber, IntPtr.Zero, out int csize, out IntPtr cdata, out _) == 0 && cdata != IntPtr.Zero)
            {
                try
                {
                    int channel = csize >= 4 ? Marshal.ReadInt32(cdata) : 0;
                    if (channel is > 0 and < 256) info.WifiChannel = channel;
                }
                finally
                {
                    WlanFreeMemory(cdata);
                }
            }
            return WlanReadResult.Ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return WlanReadResult.Unavailable;   // Windows without the wireless LAN components
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Reads a WLAN_CONNECTION_ATTRIBUTES block. Returns false (and changes nothing) when the adapter is not connected or
    /// the values are not plausible.
    /// </summary>
    public static bool ReadConnection(byte[] a, NetworkInterfaceInfo info)
    {
        if (a.Length < ConnectionAttributesSize || BitConverter.ToInt32(a, OffState) != StateConnected) return false;
        int ssidLength = BitConverter.ToInt32(a, OffSsidLength);
        int signal = BitConverter.ToInt32(a, OffSignal);
        if (ssidLength is < 0 or > 32 || signal is < 0 or > 100) return false;

        info.WifiSsid = ssidLength == 0 ? "" : System.Text.Encoding.UTF8.GetString(a, OffSsid, ssidLength);
        info.WifiSignalPercent = signal;
        info.WifiBssid = string.Join(":", Enumerate(a, OffBssid, 6));
        info.WifiRadioType = PhyName(BitConverter.ToInt32(a, OffPhyType)) ?? info.WifiRadioType;
        info.WifiReceiveRate = Mbps(BitConverter.ToUInt32(a, OffRxRate));
        info.WifiTransmitRate = Mbps(BitConverter.ToUInt32(a, OffTxRate));
        int cipher = BitConverter.ToInt32(a, OffCipher);
        info.WifiAuthentication = AuthName(BitConverter.ToInt32(a, OffAuth), cipher) ?? info.WifiAuthentication;
        info.WifiCipher = CipherName(cipher) ?? info.WifiCipher;
        return true;
    }

    private static System.Collections.Generic.IEnumerable<string> Enumerate(byte[] a, int offset, int count)
    {
        for (int i = 0; i < count; i++) yield return a[offset + i].ToString("x2");
    }

    /// <summary>Link rate as <c>netsh</c> shows it: the API gives kbit/s.</summary>
    private static string Mbps(uint kbps) => (kbps / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>DOT11_PHY_TYPE as the Wi-Fi standard's name.</summary>
    public static string? PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n",
        8 => "802.11ac",
        9 => "802.11ad",
        10 => "802.11ax",
        11 => "802.11be",
        _ => null
    };

    /// <summary>DOT11_AUTH_ALGORITHM in the words <c>netsh</c> uses (which the security check understands).</summary>
    public static string? AuthName(int auth, int cipher) => auth switch
    {
        1 => cipher is 1 or 5 or 0x101 ? "WEP" : "Open",
        2 => "Shared",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        5 => "WPA-None",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise 192 Bits",
        9 => "WPA3-Personal",
        10 => "OWE",
        11 => "WPA3-Enterprise",
        _ => null
    };

    /// <summary>DOT11_CIPHER_ALGORITHM in the words <c>netsh</c> uses.</summary>
    public static string? CipherName(int cipher) => cipher switch
    {
        0 => "None",
        1 or 5 or 0x101 => "WEP",
        2 => "TKIP",
        4 => "CCMP",
        8 => "GCMP",
        9 => "GCMP-256",
        10 => "CCMP-256",
        _ => null
    };
}
