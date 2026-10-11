using System.Net;
using System.Net.Sockets;
using NetPulse.Core.Games;

namespace NetPulse.Tests;

public class ConnectionDiscoveryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<int> Game = new HashSet<int> { 100 };

    private static RawConnection Tcp(string remote, int remotePort, int pid = 100, int localPort = 50000, ConnectionState state = ConnectionState.Established, string local = "192.168.1.20") =>
        new(TransportProtocol.Tcp, IPAddress.Parse(local), localPort, IPAddress.Parse(remote), remotePort, state, pid);

    private static RawConnection Udp(int localPort, int pid = 100, string local = "0.0.0.0") =>
        new(TransportProtocol.Udp, IPAddress.Parse(local), localPort, null, 0, ConnectionState.NotApplicable, pid);

    private static ConnectionSnapshot Snap(int second, params RawConnection[] c) => new(c, Array.Empty<string>(), T0.AddSeconds(second));

    // ---- scope classification ----

    [Theory]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("10.1.2.3", AddressScope.LocalNetwork)]
    [InlineData("172.16.0.5", AddressScope.LocalNetwork)]
    [InlineData("172.32.0.5", AddressScope.Public)]
    [InlineData("192.168.100.1", AddressScope.LocalNetwork)]
    [InlineData("169.254.3.3", AddressScope.LocalNetwork)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("0.0.0.0", AddressScope.Unspecified)]
    [InlineData("::", AddressScope.Unspecified)]
    [InlineData("224.0.0.251", AddressScope.Multicast)]
    [InlineData("255.255.255.255", AddressScope.Multicast)]
    [InlineData("ff02::1", AddressScope.Multicast)]
    [InlineData("fe80::1", AddressScope.LocalNetwork)]
    [InlineData("fd12:3456::1", AddressScope.LocalNetwork)]
    [InlineData("2606:4700::1111", AddressScope.Public)]
    [InlineData("::ffff:8.8.8.8", AddressScope.Public)]
    [InlineData("::ffff:192.168.1.1", AddressScope.LocalNetwork)]
    public void Scope_IsClassifiedCorrectly(string ip, AddressScope expected) =>
        Assert.Equal(expected, NetworkScopes.Classify(IPAddress.Parse(ip)));

    // ---- tracker ----

    [Fact]
    public void IPv4Tcp_IsRecordedWithAllFields_AndRoleIsUnknown()
    {
        var t = new ConnectionTracker();
        var update = t.Update(Snap(0, Tcp("34.1.2.3", 443)), Game);

        var c = Assert.Single(update.Active);
        Assert.Equal(TransportProtocol.Tcp, c.Protocol);
        Assert.Equal("34.1.2.3:443", c.RemoteDisplay);
        Assert.Equal("192.168.1.20:50000", c.LocalDisplay);
        Assert.Equal(ConnectionState.Established, c.State);
        Assert.Equal(100, c.Pid);
        Assert.Equal(AddressScope.Public, c.Scope);
        Assert.Equal(EndpointRole.Unknown, c.Role);
        Assert.Equal(T0, c.FirstSeenUtc);
        Assert.Equal(ConnectionChangeKind.Appeared, Assert.Single(update.Changes).Kind);
    }

    [Fact]
    public void IPv6Tcp_IsRecorded_AndFormattedWithBrackets()
    {
        var t = new ConnectionTracker();
        var raw = new RawConnection(TransportProtocol.Tcp, IPAddress.Parse("2001:db8::10"), 51000, IPAddress.Parse("2606:4700::1111"), 443, ConnectionState.Established, 100);
        var c = Assert.Single(t.Update(Snap(0, raw), Game).Active);

        Assert.Equal("[2606:4700::1111]:443", c.RemoteDisplay);
        Assert.Equal(AddressScope.Public, c.Scope);
    }

    [Fact]
    public void UdpSockets_AreKept_WithoutARemoteOrState()
    {
        var t = new ConnectionTracker();
        var update = t.Update(Snap(0, Udp(7777), Udp(7778, local: "::")), Game);

        Assert.Equal(2, update.Active.Count);
        Assert.All(update.Active, c =>
        {
            Assert.Equal(TransportProtocol.Udp, c.Protocol);
            Assert.False(c.HasRemote);
            Assert.Equal(ConnectionState.NotApplicable, c.State);
            Assert.Contains("remote peer not visible", ConnectionTracker.Describe(c));
        });
    }

    [Fact]
    public void RepeatedObservations_AreDeduplicated()
    {
        var t = new ConnectionTracker();
        t.Update(Snap(0, Tcp("34.1.2.3", 443), Udp(7777)), Game);
        var again = t.Update(Snap(2, Tcp("34.1.2.3", 443), Udp(7777)), Game);

        Assert.Empty(again.Changes);
        Assert.Equal(2, t.All.Count);
        var c = again.Active.Single(x => x.HasRemote);
        Assert.Equal(T0, c.FirstSeenUtc);
        Assert.Equal(T0.AddSeconds(2), c.LastSeenUtc);
        Assert.Equal(2, c.Observations);
    }

    [Fact]
    public void StateChange_IsReported()
    {
        var t = new ConnectionTracker();
        t.Update(Snap(0, Tcp("34.1.2.3", 443, state: ConnectionState.SynSent)), Game);
        var update = t.Update(Snap(1, Tcp("34.1.2.3", 443, state: ConnectionState.Established)), Game);

        var change = Assert.Single(update.Changes);
        Assert.Equal(ConnectionChangeKind.StateChanged, change.Kind);
        Assert.Equal(ConnectionState.SynSent, change.PreviousState);
    }

    [Fact]
    public void Disappearance_NeedsConsecutiveMisses_SoFlappingIsAbsorbed()
    {
        var t = new ConnectionTracker { MissesBeforeDisappear = 2 };
        t.Update(Snap(0, Tcp("34.1.2.3", 443)), Game);

        Assert.Empty(t.Update(Snap(1), Game).Changes);                       // one miss
        Assert.Empty(t.Update(Snap(2, Tcp("34.1.2.3", 443)), Game).Changes); // back: no events at all

        t.Update(Snap(3), Game);
        var gone = t.Update(Snap(4), Game);
        Assert.Equal(ConnectionChangeKind.Disappeared, Assert.Single(gone.Changes).Kind);
        Assert.Empty(gone.Active);
        Assert.NotNull(t.All.Single().EndedUtc);
    }

    [Fact]
    public void ShortLivedConnection_SeenOnce_IsStillRecorded()
    {
        var t = new ConnectionTracker();
        t.Update(Snap(0, Tcp("34.9.9.9", 8080)), Game);
        t.Update(Snap(1), Game);
        t.Update(Snap(2), Game);

        var c = Assert.Single(t.All);
        Assert.False(c.IsActive);
        Assert.Equal(1, c.Observations);
    }

    [Fact]
    public void OwnerExit_EndsConnectionsImmediately_WithoutWaitingForMisses()
    {
        var t = new ConnectionTracker();
        t.Update(Snap(0, Tcp("34.1.2.3", 443), Udp(7777)), Game);

        var update = t.Update(Snap(2), new HashSet<int>()); // game process gone
        Assert.Equal(2, update.Changes.Count(c => c.Kind == ConnectionChangeKind.Disappeared));
        Assert.Contains("no longer running", update.Changes[0].Description);
        Assert.Empty(update.Active);
    }

    [Fact]
    public void ProcessRestart_NewPidConnectionsAreTrackedAfterOldOnesEnd()
    {
        var t = new ConnectionTracker();
        t.Update(Snap(0, Tcp("34.1.2.3", 443, pid: 100, localPort: 50000)), Game);
        t.Update(Snap(2), new HashSet<int>());

        var update = t.Update(Snap(4, Tcp("34.1.2.3", 443, pid: 200, localPort: 50111)), new HashSet<int> { 200 });

        Assert.Equal(ConnectionChangeKind.Appeared, Assert.Single(update.Changes).Kind);
        Assert.Equal(200, Assert.Single(update.Active).Pid);
        Assert.Equal(2, t.All.Count);
    }

    [Fact]
    public void OtherProcessesSockets_AreIgnored()
    {
        var t = new ConnectionTracker();
        var update = t.Update(Snap(0, Tcp("1.1.1.1", 443, pid: 999), Udp(5353, pid: 999)), Game);

        Assert.Empty(update.Active);
        Assert.Empty(update.Changes);
    }

    [Fact]
    public void ClosingSockets_AreNotTreatedAsLiveEndpoints()
    {
        var t = new ConnectionTracker();
        var update = t.Update(Snap(0, Tcp("34.1.2.3", 443, state: ConnectionState.TimeWait)), Game);
        Assert.Empty(update.Active);
    }

    [Fact]
    public void Listener_HasNoRemoteEndpoint()
    {
        var t = new ConnectionTracker();
        var listener = new RawConnection(TransportProtocol.Tcp, IPAddress.Any, 27015, null, 0, ConnectionState.Listen, 100);
        var c = Assert.Single(t.Update(Snap(0, listener), Game).Active);
        Assert.False(c.HasRemote);
    }

    [Fact]
    public void SourceWarnings_AreSurfacedToTheCaller()
    {
        var t = new ConnectionTracker();
        var snap = new ConnectionSnapshot(Array.Empty<RawConnection>(), new[] { "IPv6 UDP table could not be read (Windows error 5)." }, T0);
        Assert.Single(t.Update(snap, Game).Warnings);
    }

    // ---- real Windows API (no admin needed): sockets created by this very process must be found ----

    [Fact]
    public void WindowsSource_FindsThisProcessesIPv4TcpAndUdpSockets()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, listenPort);
        using var accepted = listener.AcceptTcpClient();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var snapshot = new WindowsConnectionSource().Capture();
        var mine = snapshot.Connections.Where(c => c.Pid == Environment.ProcessId).ToList();

        Assert.True(snapshot.Warnings.Count == 0, string.Join("; ", snapshot.Warnings));
        Assert.Contains(mine, c => c.Protocol == TransportProtocol.Tcp && c.State == ConnectionState.Listen && c.LocalPort == listenPort && c.RemoteAddress == null);
        Assert.Contains(mine, c => c.Protocol == TransportProtocol.Tcp && c.State == ConnectionState.Established
            && c.LocalPort == clientPort && c.RemotePort == listenPort && IPAddress.Loopback.Equals(c.RemoteAddress));
        Assert.Contains(mine, c => c.Protocol == TransportProtocol.Udp && c.LocalPort == udpPort && c.RemoteAddress == null && c.State == ConnectionState.NotApplicable);
    }

    [Fact]
    public void WindowsSource_FindsThisProcessesIPv6Sockets()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        client.Connect(IPAddress.IPv6Loopback, listenPort);
        using var accepted = listener.AcceptTcpClient();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var mine = new WindowsConnectionSource().Capture().Connections.Where(c => c.Pid == Environment.ProcessId).ToList();

        Assert.Contains(mine, c => c.Protocol == TransportProtocol.Tcp && c.LocalPort == clientPort && c.RemotePort == listenPort
            && c.State == ConnectionState.Established && IPAddress.IPv6Loopback.Equals(c.RemoteAddress));
        Assert.Contains(mine, c => c.Protocol == TransportProtocol.Udp && c.LocalPort == udpPort && c.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6);
    }

    [Fact]
    public void WindowsSource_RealSnapshotFeedsTheTracker()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        var tracker = new ConnectionTracker();
        var update = tracker.Update(new WindowsConnectionSource().Capture(), new HashSet<int> { Environment.ProcessId });

        Assert.Contains(update.Active, c => c.HasRemote && c.Scope == AddressScope.Loopback);
        Assert.All(update.Active, c => Assert.Equal(Environment.ProcessId, c.Pid));
    }
}
