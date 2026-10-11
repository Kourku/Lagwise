using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetPulse.Core.Games;

public enum TransportProtocol { Tcp, Udp }

public enum ConnectionState
{
    /// <summary>UDP has no connection state.</summary>
    NotApplicable,
    Unknown,
    Closed,
    Listen,
    SynSent,
    SynReceived,
    Established,
    FinWait1,
    FinWait2,
    CloseWait,
    Closing,
    LastAck,
    TimeWait,
    DeleteTcb
}

public enum AddressScope { Unspecified, Loopback, LocalNetwork, Multicast, Public }

/// <summary>
/// What an endpoint is for. Deliberately only <see cref="Unknown"/>: nothing NetPulse can observe (addresses, ports,
/// protocol) is reliable evidence that an endpoint is a match server rather than login, telemetry, voice or CDN.
/// </summary>
public enum EndpointRole { Unknown }

/// <summary>One socket entry as reported by Windows. UDP entries have no remote endpoint.</summary>
public sealed record RawConnection(
    TransportProtocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort,
    ConnectionState State,
    int Pid);

public sealed record ConnectionSnapshot(IReadOnlyList<RawConnection> Connections, IReadOnlyList<string> Warnings, DateTime CapturedUtc);

public interface IConnectionSource
{
    ConnectionSnapshot Capture();
}

public static class NetworkScopes
{
    public static AddressScope Classify(IPAddress? address)
    {
        if (address == null) return AddressScope.Unspecified;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return AddressScope.Loopback;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return AddressScope.Unspecified;

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] >= 224) return AddressScope.Multicast; // 224/4 multicast and 255.255.255.255 broadcast
            if (b[0] == 10) return AddressScope.LocalNetwork;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return AddressScope.LocalNetwork;
            if (b[0] == 192 && b[1] == 168) return AddressScope.LocalNetwork;
            if (b[0] == 169 && b[1] == 254) return AddressScope.LocalNetwork;
            return AddressScope.Public;
        }

        if (b[0] == 0xff) return AddressScope.Multicast;
        if (b[0] == 0xfe && (b[1] & 0xC0) == 0x80) return AddressScope.LocalNetwork; // fe80::/10 link-local
        if ((b[0] & 0xFE) == 0xFC) return AddressScope.LocalNetwork;                 // fc00::/7 unique local
        return AddressScope.Public;
    }
}

/// <summary>
/// Reads the TCP and UDP socket tables with owning PIDs through iphlpapi (GetExtendedTcpTable / GetExtendedUdpTable),
/// for IPv4 and IPv6. No administrator rights are needed. API failures become warnings, never exceptions.
/// Windows reports UDP sockets only by local endpoint, so UDP entries never have a remote address.
/// </summary>
public sealed class WindowsConnectionSource : IConnectionSource
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    public ConnectionSnapshot Capture()
    {
        var connections = new List<RawConnection>();
        var warnings = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("Connection discovery uses Windows networking APIs and is unavailable on this platform.");
            return new ConnectionSnapshot(connections, warnings, DateTime.UtcNow);
        }

        foreach (int af in new[] { AfInet, AfInet6 })
        {
            string family = af == AfInet ? "IPv4" : "IPv6";
            try
            {
                if (!ReadTable(af, TcpTableOwnerPidAll, true, connections, out uint tcpError))
                    warnings.Add($"{family} TCP table could not be read (Windows error {tcpError}).");
                if (!ReadTable(af, UdpTableOwnerPid, false, connections, out uint udpError))
                    warnings.Add($"{family} UDP table could not be read (Windows error {udpError}).");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                warnings.Add($"{family} socket tables are unavailable: {ex.Message}");
            }
        }

        return new ConnectionSnapshot(connections, warnings, DateTime.UtcNow);
    }

    private static bool ReadTable(int af, int tableClass, bool tcp, List<RawConnection> into, out uint error)
    {
        int size = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // The table can grow between calls, so on "insufficient buffer" resize (with slack) and retry.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                error = tcp ? GetExtendedTcpTable(buffer, ref size, false, af, tableClass, 0)
                            : GetExtendedUdpTable(buffer, ref size, false, af, tableClass, 0);

                if (error == 0)
                {
                    if (buffer == IntPtr.Zero) return true; // empty table
                    ParseRows(buffer, af, tcp, into);
                    return true;
                }

                if (error != ErrorInsufficientBuffer) return false;

                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
            }

            error = ErrorInsufficientBuffer;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ParseRows(IntPtr buffer, int af, bool tcp, List<RawConnection> into)
    {
        int count = Marshal.ReadInt32(buffer);
        IntPtr row = buffer + 4;
        int rowSize = (tcp, af) switch
        {
            (true, AfInet) => 24,
            (true, _) => 56,
            (false, AfInet) => 12,
            _ => 28
        };

        for (int i = 0; i < count; i++, row += rowSize)
        {
            into.Add(tcp ? ParseTcp(row, af) : ParseUdp(row, af));
        }
    }

    private static int Port(int raw) => (ushort)((raw & 0xFF) << 8 | (raw >> 8) & 0xFF);

    private static IPAddress Addr6(IntPtr p, int offset, uint scope)
    {
        var bytes = new byte[16];
        Marshal.Copy(p + offset, bytes, 0, 16);
        return new IPAddress(bytes, scope);
    }

    private static RawConnection ParseTcp(IntPtr row, int af)
    {
        if (af == AfInet)
        {
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
            var state = Marshal.ReadInt32(row, 0);
            var local = new IPAddress((uint)Marshal.ReadInt32(row, 4));
            int localPort = Port(Marshal.ReadInt32(row, 8));
            var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
            int remotePort = Port(Marshal.ReadInt32(row, 16));
            int pid = Marshal.ReadInt32(row, 20);
            return Build(TransportProtocol.Tcp, local, localPort, remote, remotePort, MapState(state), pid);
        }
        else
        {
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
            var local = Addr6(row, 0, (uint)Marshal.ReadInt32(row, 16));
            int localPort = Port(Marshal.ReadInt32(row, 20));
            var remote = Addr6(row, 24, (uint)Marshal.ReadInt32(row, 40));
            int remotePort = Port(Marshal.ReadInt32(row, 44));
            int state = Marshal.ReadInt32(row, 48);
            int pid = Marshal.ReadInt32(row, 52);
            return Build(TransportProtocol.Tcp, local, localPort, remote, remotePort, MapState(state), pid);
        }
    }

    private static RawConnection ParseUdp(IntPtr row, int af)
    {
        if (af == AfInet)
        {
            // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
            return new RawConnection(TransportProtocol.Udp, new IPAddress((uint)Marshal.ReadInt32(row, 0)),
                Port(Marshal.ReadInt32(row, 4)), null, 0, ConnectionState.NotApplicable, Marshal.ReadInt32(row, 8));
        }

        // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid
        return new RawConnection(TransportProtocol.Udp, Addr6(row, 0, (uint)Marshal.ReadInt32(row, 16)),
            Port(Marshal.ReadInt32(row, 20)), null, 0, ConnectionState.NotApplicable, Marshal.ReadInt32(row, 24));
    }

    private static RawConnection Build(TransportProtocol protocol, IPAddress local, int localPort, IPAddress remote, int remotePort, ConnectionState state, int pid)
    {
        // Listening sockets report a zero remote address; there is no remote endpoint to record.
        bool hasRemote = state != ConnectionState.Listen && remotePort != 0
            && !remote.Equals(IPAddress.Any) && !remote.Equals(IPAddress.IPv6Any);
        return new RawConnection(protocol, local, localPort, hasRemote ? remote : null, hasRemote ? remotePort : 0, state, pid);
    }

    internal static ConnectionState MapState(int state) => state switch
    {
        1 => ConnectionState.Closed,
        2 => ConnectionState.Listen,
        3 => ConnectionState.SynSent,
        4 => ConnectionState.SynReceived,
        5 => ConnectionState.Established,
        6 => ConnectionState.FinWait1,
        7 => ConnectionState.FinWait2,
        8 => ConnectionState.CloseWait,
        9 => ConnectionState.Closing,
        10 => ConnectionState.LastAck,
        11 => ConnectionState.TimeWait,
        12 => ConnectionState.DeleteTcb,
        _ => ConnectionState.Unknown
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}

/// <summary>A process-associated socket observed over time (deduplicated by protocol + local + remote endpoint).</summary>
public sealed class TrackedConnection
{
    public required string Id { get; init; }
    public TransportProtocol Protocol { get; init; }
    public required IPEndPoint Local { get; init; }
    /// <summary>Null for UDP sockets and TCP listeners: Windows does not expose a remote peer for them.</summary>
    public IPEndPoint? Remote { get; init; }
    public ConnectionState State { get; set; }
    public int Pid { get; set; }
    public AddressScope Scope { get; init; }
    public EndpointRole Role => EndpointRole.Unknown;
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public int Observations { get; set; }
    public bool IsActive => EndedUtc == null;
    public bool HasRemote => Remote != null;
    internal int MissedUpdates { get; set; }

    public string RemoteDisplay => Remote == null ? "(no remote peer visible)" : FormatEndpoint(Remote);
    public string LocalDisplay => FormatEndpoint(Local);

    public static string FormatEndpoint(IPEndPoint ep) =>
        ep.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ep.Address.ToString().Split('%')[0]}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";
}

public enum ConnectionChangeKind { Appeared, StateChanged, Disappeared }

public sealed record ConnectionChange(ConnectionChangeKind Kind, TrackedConnection Connection, ConnectionState? PreviousState, DateTime AtUtc, string Description);

public sealed record ConnectionUpdate(
    IReadOnlyList<ConnectionChange> Changes,
    IReadOnlyList<TrackedConnection> Active,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns repeated socket snapshots into a deduplicated set of connections plus appeared / changed / disappeared events.
/// Only sockets owned by the supplied game PIDs are tracked. A connection must be missing for
/// <see cref="MissesBeforeDisappear"/> consecutive snapshots before it is reported as gone, which absorbs flapping.
/// Sockets are only visible while they exist at a polling instant, so very short-lived connections can be missed.
/// </summary>
public sealed class ConnectionTracker
{
    private readonly Dictionary<string, TrackedConnection> _tracked = new();
    private const int MaxTracked = 2000;

    public int MissesBeforeDisappear { get; set; } = 2;

    public IReadOnlyList<TrackedConnection> All => _tracked.Values.ToList();

    public static string KeyFor(RawConnection c) =>
        $"{c.Protocol}|{c.LocalAddress}:{c.LocalPort}|{(c.RemoteAddress == null ? "-" : c.RemoteAddress + ":" + c.RemotePort)}";

    public ConnectionUpdate Update(ConnectionSnapshot snapshot, IReadOnlySet<int> gamePids)
    {
        var now = snapshot.CapturedUtc;
        var changes = new List<ConnectionChange>();
        var seen = new HashSet<string>();

        foreach (var raw in snapshot.Connections)
        {
            if (!gamePids.Contains(raw.Pid)) continue;
            // sockets that are closing are not live endpoints; existing entries are closed out below
            if (raw.State is ConnectionState.Closed or ConnectionState.TimeWait or ConnectionState.DeleteTcb) continue;

            string key = KeyFor(raw);
            seen.Add(key);

            if (_tracked.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.Observations++;
                existing.MissedUpdates = 0;
                existing.Pid = raw.Pid;

                if (!existing.IsActive)
                {
                    existing.EndedUtc = null;
                    changes.Add(new(ConnectionChangeKind.Appeared, existing, null, now, $"{Describe(existing)} was observed again."));
                }
                if (existing.State != raw.State)
                {
                    var previous = existing.State;
                    existing.State = raw.State;
                    changes.Add(new(ConnectionChangeKind.StateChanged, existing, previous, now,
                        $"{Describe(existing)} changed state: {previous} -> {raw.State}."));
                }
                continue;
            }

            var created = new TrackedConnection
            {
                Id = key,
                Protocol = raw.Protocol,
                Local = new IPEndPoint(raw.LocalAddress, raw.LocalPort),
                Remote = raw.RemoteAddress == null ? null : new IPEndPoint(raw.RemoteAddress, raw.RemotePort),
                State = raw.State,
                Pid = raw.Pid,
                Scope = raw.RemoteAddress == null ? NetworkScopes.Classify(raw.LocalAddress) : NetworkScopes.Classify(raw.RemoteAddress),
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Observations = 1
            };
            _tracked[key] = created;
            changes.Add(new(ConnectionChangeKind.Appeared, created, null, now, $"New {Describe(created)} observed."));
        }

        foreach (var conn in _tracked.Values.Where(c => c.IsActive && !seen.Contains(c.Id)).ToList())
        {
            bool ownerGone = !gamePids.Contains(conn.Pid);
            conn.MissedUpdates++;
            if (ownerGone || conn.MissedUpdates >= MissesBeforeDisappear)
            {
                conn.EndedUtc = now;
                changes.Add(new(ConnectionChangeKind.Disappeared, conn, null, now,
                    ownerGone ? $"{Describe(conn)} ended because its game process is no longer running."
                              : $"{Describe(conn)} is no longer observed."));
            }
        }

        Trim();
        return new ConnectionUpdate(changes, _tracked.Values.Where(c => c.IsActive).ToList(), snapshot.Warnings);
    }

    public void Clear() => _tracked.Clear();

    private void Trim()
    {
        if (_tracked.Count <= MaxTracked) return;
        foreach (var old in _tracked.Values.Where(c => !c.IsActive).OrderBy(c => c.EndedUtc).Take(_tracked.Count - MaxTracked).ToList())
            _tracked.Remove(old.Id);
    }

    public static string Describe(TrackedConnection c) => c.Remote == null
        ? $"{c.Protocol.ToString().ToUpperInvariant()} socket {c.LocalDisplay} (remote peer not visible)"
        : $"{c.Protocol.ToString().ToUpperInvariant()} connection to {c.RemoteDisplay}";
}
