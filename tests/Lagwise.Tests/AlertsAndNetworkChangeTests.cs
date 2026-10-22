using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class AlertsAndNetworkChangeTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);

    private static NetworkInterfaceInfo Wired(string ip = "192.168.1.20", string gw = "192.168.1.1") => new()
    {
        Id = "{eth}", Name = "Ethernet", InterfaceType = "Ethernet", OperationalStatus = "Up",
        Ipv4Address = ip, Ipv4Gateway = gw, DnsServers = new() { "192.168.1.1" }
    };

    private static NetworkInterfaceInfo Wifi(string ssid = "Home", string bssid = "aa:aa:aa:aa:aa:01", int signal = 80) => new()
    {
        Id = "{wifi}", Name = "Wi-Fi", InterfaceType = "Wireless80211", OperationalStatus = "Up", IsWifi = true,
        Ipv4Address = "192.168.1.30", Ipv4Gateway = "192.168.1.1", DnsServers = new() { "192.168.1.1" },
        WifiSsid = ssid, WifiBssid = bssid, WifiSignalPercent = signal
    };

    // ---- network change detection ----

    [Fact]
    public void SameConnection_WithDifferentSignalOrRate_IsNotAChange()
    {
        var before = Wifi(signal: 80);
        var after = Wifi(signal: 52);
        after.WifiReceiveRate = "400";
        Assert.Null(NetworkChangeDescriber.Describe(before, after));
        Assert.Null(NetworkChangeDescriber.Describe(Wired(), Wired()));
    }

    [Fact]
    public void SwitchingFromCableToWifi_IsAWarning_ThatNamesBothKinds()
    {
        var c = NetworkChangeDescriber.Describe(Wired(), Wifi());
        Assert.NotNull(c);
        Assert.Equal("Warning", c!.Severity);
        Assert.Contains("wired Ethernet to Wi-Fi", c.Description);
    }

    [Fact]
    public void LosingTheAddress_IsADisconnect()
    {
        var down = Wired(ip: "");
        var c = NetworkChangeDescriber.Describe(Wired(), down);
        Assert.True(c!.Disconnected);
        Assert.Equal("Critical", c.Severity);
        Assert.Null(NetworkChangeDescriber.Describe(down, down));
        Assert.Contains("Connected via", NetworkChangeDescriber.Describe(down, Wired())!.Description);
    }

    [Fact]
    public void NewRouterOrAddress_IsReported_DnsOnlyIsInfo()
    {
        var c = NetworkChangeDescriber.Describe(Wired(), Wired(ip: "10.0.0.5", gw: "10.0.0.1"));
        Assert.Contains("IP address 192.168.1.20 to 10.0.0.5", c!.Description);
        Assert.Contains("router 192.168.1.1 to 10.0.0.1", c.Description);
        Assert.Equal("Warning", c.Severity);

        var dns = Wired();
        dns.DnsServers = new() { "1.1.1.1" };
        Assert.Equal("Info", NetworkChangeDescriber.Describe(Wired(), dns)!.Severity);
    }

    [Fact]
    public void WifiAccessPointChange_IsRoaming_AndADifferentNetworkIsNot()
    {
        var roam = NetworkChangeDescriber.Describe(Wifi(), Wifi(bssid: "aa:aa:aa:aa:aa:02", signal: 61));
        Assert.True(roam!.Roamed);
        Assert.Contains("signal 61%", roam.Description);

        var other = NetworkChangeDescriber.Describe(Wifi(), Wifi(ssid: "Neighbour", bssid: "bb:bb:bb:bb:bb:01"));
        Assert.False(other!.Roamed);
        Assert.Contains("\"Home\" to \"Neighbour\"", other.Description);
    }

    private sealed class FakeDiscovery : NetworkDiscoveryService
    {
        public NetworkInterfaceInfo Next { get; set; } = Wired();
        public override NetworkInterfaceInfo GetActiveInterfaceInfo() => Next;
    }

    [Fact]
    public void Refresh_RaisesNetworkChangedOnlyForRealChanges()
    {
        var d = new FakeDiscovery();
        var changes = new List<NetworkChange>();
        int refreshed = 0;
        d.NetworkChanged += (_, c) => changes.Add(c);
        d.InterfaceRefreshed += (_, _) => refreshed++;

        d.Refresh();                    // first reading is only the baseline
        d.Next = Wired();
        d.Refresh();                    // nothing changed
        d.Next = Wifi();
        d.Refresh();                    // cable to Wi-Fi

        Assert.Equal(3, refreshed);
        Assert.Single(changes);
        Assert.Contains("Wi-Fi", changes[0].Description);
    }

    // ---- desktop alerts ----

    private static ConnectionEvent Evt(string type, double seconds, string target = "", string severity = "Warning", double? value = null) => new()
    {
        EventType = type, Timestamp = T0.AddSeconds(seconds), TargetName = target, Severity = severity, Description = $"{type} happened", MetricValue = value
    };

    [Fact]
    public void Disconnect_ThenRestore_GivesTwoAlerts()
    {
        var p = new AlertPolicy(T0);
        Assert.Equal("Connection lost", p.Evaluate(Evt("Disconnected", 100, SessionRecorder.ConnectivityScope, "Critical"))!.Title);
        var back = p.Evaluate(Evt("Restored", 140, SessionRecorder.ConnectivityScope, "Info"));
        Assert.Equal("Back online", back!.Title);
        Assert.False(back.IsProblem);
        // a second restore without a new disconnect says nothing
        Assert.Null(p.Evaluate(Evt("Restored", 150, SessionRecorder.ConnectivityScope, "Info")));
    }

    [Fact]
    public void RestoreOfASingleTarget_OrWithoutAnAlertedDisconnect_IsSilent()
    {
        var p = new AlertPolicy(T0);
        Assert.Null(p.Evaluate(Evt("Restored", 100, "Gateway", "Info")));
        Assert.Null(p.Evaluate(Evt("Restored", 100, SessionRecorder.ConnectivityScope, "Info")));
    }

    [Fact]
    public void NothingIsShown_DuringWarmUp()
    {
        var p = new AlertPolicy(T0);
        Assert.Null(p.Evaluate(Evt("GatewayDegraded", 10, "Router", "Critical")));
        Assert.NotNull(p.Evaluate(Evt("GatewayDegraded", 60, "Router", "Critical")));
    }

    [Fact]
    public void EachKind_IsLimitedByTheCooldown()
    {
        var p = new AlertPolicy(T0);
        Assert.NotNull(p.Evaluate(Evt("PacketLoss", 60, "Cloudflare", value: 12)));
        Assert.Null(p.Evaluate(Evt("PacketLoss", 90, "Google")));
        Assert.NotNull(p.Evaluate(Evt("GatewayDegraded", 95, "Router", "Critical")));   // a different kind is not held back
        Assert.NotNull(p.Evaluate(Evt("PacketLoss", 60 + 181, "Google")));
    }

    [Fact]
    public void NoisyEvents_NeverAlert()
    {
        var p = new AlertPolicy(T0);
        foreach (var type in new[] { "LatencySpike", "RouteChange", "WifiRoam", "LagMarker", "GameProcess" })
            Assert.Null(p.Evaluate(Evt(type, 100)));
        Assert.Null(p.Evaluate(Evt("NetworkChange", 100, severity: "Info")));
        Assert.Equal("Your network changed", p.Evaluate(Evt("NetworkChange", 100))!.Title);
    }

    [Fact]
    public void LossAlert_NamesTheServerAndShare()
    {
        var a = new AlertPolicy(T0).Evaluate(Evt("PacketLoss", 100, "Cloudflare DNS", value: 12.4));
        Assert.Contains("Cloudflare DNS: 12% of probes", a!.Message);
    }
}
