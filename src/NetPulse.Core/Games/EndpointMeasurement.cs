using System.Collections.Generic;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Games;

public enum EndpointMethod { Icmp, TcpConnect }

/// <param name="IsAvailable">False when the method would not produce a meaningful result for this endpoint.</param>
public sealed record MeasurementOption(EndpointMethod Method, bool IsAvailable, string Explanation);

/// <summary>Decides which measurement methods are meaningful for an observed endpoint and builds the probe target.</summary>
public static class EndpointMeasurementAdvisor
{
    public const string IcmpCaveat =
        "ICMP ping measures the network path to this address. Many game and cloud servers filter or ignore ICMP, so 'no reply' does not mean the endpoint is offline.";

    public static IReadOnlyList<MeasurementOption> Advise(TrackedConnection c)
    {
        if (c.Remote == null)
        {
            const string why = "Windows does not show the remote peer of UDP sockets or listeners, so there is no address to measure.";
            return new[]
            {
                new MeasurementOption(EndpointMethod.Icmp, false, why),
                new MeasurementOption(EndpointMethod.TcpConnect, false, why)
            };
        }

        string? notRemote = c.Scope switch
        {
            AddressScope.Loopback => "This is a connection to your own PC, not a remote game service.",
            AddressScope.LocalNetwork => "This address is on your local network (or link-local), not an Internet game service. The router is already measured separately.",
            AddressScope.Multicast => "Multicast and broadcast addresses cannot be pinged meaningfully.",
            AddressScope.Unspecified => "This is not a usable remote address.",
            _ => null
        };

        if (notRemote != null)
        {
            return new[]
            {
                new MeasurementOption(EndpointMethod.Icmp, false, notRemote),
                new MeasurementOption(EndpointMethod.TcpConnect, false, notRemote)
            };
        }

        var tcp = c.Protocol == TransportProtocol.Tcp
            ? new MeasurementOption(EndpointMethod.TcpConnect, true,
                $"Opens a short new TCP connection to port {c.Remote.Port} and times the handshake. It measures that service, which may not be the machine running your match.")
            : new MeasurementOption(EndpointMethod.TcpConnect, false, "TCP probing does not apply to a UDP endpoint.");

        return new[] { new MeasurementOption(EndpointMethod.Icmp, true, IcmpCaveat), tcp };
    }

    /// <summary>Returns the probe target, or null (with the reason) when the method is not meaningful for the endpoint.</summary>
    public static TargetConfig? BuildTarget(TrackedConnection c, EndpointMethod method, out string message)
    {
        foreach (var option in Advise(c))
        {
            if (option.Method != method) continue;
            if (!option.IsAvailable)
            {
                message = option.Explanation;
                return null;
            }
        }

        var remote = c.Remote!;
        string host = remote.Address.ToString().Split('%')[0];
        message = method == EndpointMethod.Icmp ? IcmpCaveat : $"TCP connect timing to port {remote.Port}.";
        return new TargetConfig
        {
            Id = TargetId(c, method),
            Name = $"{host} ({(method == EndpointMethod.Icmp ? "ICMP" : "TCP " + remote.Port)})",
            Host = host,
            Port = method == EndpointMethod.TcpConnect ? remote.Port : 0,
            Protocol = method == EndpointMethod.Icmp ? NetworkProtocol.Icmp : NetworkProtocol.Tcp,
            Kind = MeasurementKind.GameProcessEndpoint,
            Description = "Endpoint observed on the game process. Role unknown; not confirmed as the match server."
        };
    }

    public static string TargetId(TrackedConnection c, EndpointMethod method) =>
        $"game:{method}:{c.Remote!.Address}:{c.Remote.Port}";
}
