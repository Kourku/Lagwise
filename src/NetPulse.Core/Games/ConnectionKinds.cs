namespace NetPulse.Core.Games;

/// <summary>
/// What a socket most probably carries, judged only from protocol, address scope and port convention.
/// This is a hint for reading the list, not an identification: the endpoint's role stays "Unknown"
/// and nothing here is ever presented as the match server.
/// </summary>
public enum ConnectionKind
{
    /// <summary>Talks only to another program on this PC (for example a game's local data API).</summary>
    LocalOnly,
    /// <summary>A device on the home network.</summary>
    LocalNetwork,
    /// <summary>TCP to a public address on a web port (443, 80, 8443): logins, store, chat, telemetry, patching.</summary>
    WebService,
    /// <summary>A UDP socket. Real-time match traffic of most games is UDP; Windows does not reveal its remote peer.</summary>
    UdpSocket,
    /// <summary>A socket waiting for incoming connections.</summary>
    Listening,
    /// <summary>TCP to a public address on another port.</summary>
    OtherTcp,
    /// <summary>Multicast/broadcast or an unspecified address.</summary>
    Other
}

public sealed record ConnectionKindInfo(ConnectionKind Kind, string Label, string Explanation);

public static class ConnectionKinds
{
    private static readonly int[] WebPorts = { 443, 80, 8443 };

    public static bool IsWebPort(int port) => System.Array.IndexOf(WebPorts, port) >= 0;

    public static ConnectionKindInfo Describe(TrackedConnection c) =>
        Describe(c.Protocol, c.Scope, c.Remote?.Port, c.State);

    public static ConnectionKindInfo Describe(TransportProtocol protocol, AddressScope scope, int? remotePort, ConnectionState state)
    {
        if (protocol == TransportProtocol.Udp)
            return new(ConnectionKind.UdpSocket, "UDP socket (server hidden)",
                "Most games send match traffic over UDP. Windows only reports the local side of a UDP socket, so the server it talks to cannot be shown here.");

        if (state == ConnectionState.Listen)
            return new(ConnectionKind.Listening, "Waiting for connections",
                "A socket the game opened to accept connections, usually from other programs on this PC.");

        return scope switch
        {
            AddressScope.Loopback => new(ConnectionKind.LocalOnly, "Local to this PC",
                "Traffic between programs on this PC (for example the game's own local data API). It never leaves your computer."),
            AddressScope.LocalNetwork => new(ConnectionKind.LocalNetwork, "Home network",
                "A device on your local network, such as your router."),
            AddressScope.Public when remotePort is int p && IsWebPort(p) => new(ConnectionKind.WebService, "Web / API service (HTTPS)",
                $"TCP port {p} carries web traffic: logins, store, chat, telemetry or downloads. It is very unlikely to be the match itself, which normally uses UDP."),
            AddressScope.Public => new(ConnectionKind.OtherTcp, "Other internet service",
                "A TCP connection to the internet on a non-web port. Its purpose is unknown."),
            _ => new(ConnectionKind.Other, "Other", "Multicast, broadcast or an unspecified address.")
        };
    }
}
