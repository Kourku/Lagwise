using System.Net;
using Lagwise.Core;
using Lagwise.Core.Models;
using Lagwise.Core.Security;
using Lagwise.Core.Services;
using Lagwise.Core.Sites;

namespace Lagwise.Tests;

/// <summary>The security check, report privacy and the hardening helpers (0.16).</summary>
public class SecurityTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);

    private static FirewallState AllOn(int active = FirewallState.Public) => new(active, true, true, true);

    public sealed class Builder
    {
        public FirewallState? Firewall = AllOn();
        public bool OnWifi;
        public string? Auth, Cipher;
        public List<Listener>? Listeners = new() { new("svchost", 135, IPAddress.Any), new("System", 445, IPAddress.Any) };
        public DnsAnswer? MadeUp = new(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 5);
        public DnsAnswer? MadeUpPublic = new(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 5);
        public List<TlsObservation>? Tls = SecurityCheckService.TestSites.Select(s => new TlsObservation(s, true, true, "GlobalSign Root CA", true)).ToList();
        public bool? Upnp;
        public SecurityEvidence Build() => new()
        {
            Firewall = Firewall, OnWifi = OnWifi, WifiAuthentication = Auth, WifiCipher = Cipher, Listeners = Listeners,
            MadeUpNameYourDns = MadeUp, MadeUpNamePublicDns = MadeUpPublic, Tls = Tls, Upnp = Upnp
        };
    }

    private static SecurityEvidence Safe(Action<Builder>? change = null)
    {
        var b = new Builder();
        change?.Invoke(b);
        return b.Build();
    }

    private static SecurityItem Item(SecurityEvidence e, string id) => SecurityCheck.Evaluate(e, T0).Items.Single(i => i.Id == id);

    [Fact]
    public void ASafePC_IsGood_AndInformationLinesNeverMakeItWorse()
    {
        var r = SecurityCheck.Evaluate(Safe(b => b.Upnp = true), T0);
        Assert.Equal(SecurityStatus.Good, r.Overall);
        Assert.Equal(SecurityStatus.Info, r.Items.Single(i => i.Id == "upnp").Status);
        Assert.Equal("Nothing on this PC or its connection looks unsafe.", r.Headline);
    }

    [Fact]
    public void TheFirewall_OffForThisNetwork_IsAProblem_OffForAnother_IsWorthALook()
    {
        Assert.Equal(SecurityStatus.Poor, Item(Safe(b => b.Firewall = new(FirewallState.Public, true, true, false)), "firewall").Status);
        var other = Item(Safe(b => b.Firewall = new(FirewallState.Public, true, false, true)), "firewall");
        Assert.Equal(SecurityStatus.Fair, other.Status);
        Assert.Contains("off for Private networks", other.Detail);
        Assert.Equal(SecurityStatus.Unknown, Item(Safe(b => b.Firewall = null), "firewall").Status);
    }

    [Fact]
    public void APrivateNetwork_IsFineAtHome_ButNotOnOpenWifi()
    {
        Assert.Equal(SecurityStatus.Good, Item(Safe(), "profile").Status);
        Assert.Equal(SecurityStatus.Info, Item(Safe(b => b.Firewall = AllOn(FirewallState.Private)), "profile").Status);
        var open = Safe(b => { b.Firewall = AllOn(FirewallState.Private); b.OnWifi = true; b.Auth = "Open"; b.Cipher = "None"; });
        Assert.Equal(SecurityStatus.Poor, Item(open, "profile").Status);
        Assert.Equal(SecurityStatus.Poor, Item(open, "wifi").Status);
    }

    [Theory]
    [InlineData("WPA2-Personal", "CCMP", SecurityCheck.WifiProtection.Wpa2)]
    [InlineData("WPA3-Personal", "GCMP-256", SecurityCheck.WifiProtection.Wpa3)]
    [InlineData("WPA2-Personal", "TKIP", SecurityCheck.WifiProtection.Weak)]
    [InlineData("WPA-Personal", "TKIP", SecurityCheck.WifiProtection.Weak)]
    [InlineData("WEP", "WEP", SecurityCheck.WifiProtection.Weak)]
    [InlineData("Open", "None", SecurityCheck.WifiProtection.Open)]
    [InlineData("Ouvrir", "Aucun", SecurityCheck.WifiProtection.Open)]
    [InlineData(null, null, SecurityCheck.WifiProtection.Unknown)]
    public void WifiProtection_IsReadFromWhatWindowsSays(string? auth, string? cipher, SecurityCheck.WifiProtection expected) =>
        Assert.Equal(expected, SecurityCheck.WifiKind(auth, cipher));

    [Fact]
    public void TheWifiDetails_AreReadFromNetsh_InEnglishAndFrench()
    {
        var en = new NetworkInterfaceInfo();
        NetworkDiscoveryService.ParseWlanInterfaces("    Name                   : Wi-Fi\r\n    SSID                   : Home\r\n    BSSID                  : aa:bb:cc:dd:ee:ff\r\n    Radio type             : 802.11ax\r\n    Authentication         : WPA3-Personal\r\n    Cipher                 : CCMP\r\n    Channel                : 36\r\n    Signal                 : 86%\r\n", en);
        Assert.Equal("Home", en.WifiSsid);
        Assert.Equal("aa:bb:cc:dd:ee:ff", en.WifiBssid);
        Assert.Equal("WPA3-Personal", en.WifiAuthentication);
        Assert.Equal("CCMP", en.WifiCipher);
        Assert.Equal(86, en.WifiSignalPercent);
        Assert.Equal(36, en.WifiChannel);

        var fr = new NetworkInterfaceInfo();
        NetworkDiscoveryService.ParseWlanInterfaces("    Authentification       : WPA2 - Personnel\r\n    Chiffrement            : CCMP\r\n", fr);
        Assert.Equal("WPA2 - Personnel", fr.WifiAuthentication);
        Assert.Equal(SecurityCheck.WifiProtection.Wpa2, SecurityCheck.WifiKind(fr.WifiAuthentication, fr.WifiCipher));
    }

    [Fact]
    public void RemoteAccessOpenToTheNetwork_IsWorthALook_AndAProblemWithoutFirewall()
    {
        var item = Item(Safe(b => b.Listeners!.Add(new("svchost", 3389, IPAddress.Any))), "listening");
        Assert.Equal(SecurityStatus.Fair, item.Status);
        Assert.Contains("Remote Desktop (port 3389", item.Detail);
        var noFirewall = Safe(b => { b.Listeners!.Add(new("svchost", 3389, IPAddress.Any)); b.Firewall = new(FirewallState.Public, true, true, false); });
        Assert.Equal(SecurityStatus.Poor, Item(noFirewall, "listening").Status);
    }

    [Fact]
    public void ProgramsWaitingForConnections_AreListed_WindowsOwnServicesAreNot()
    {
        Assert.Equal(SecurityStatus.Good, Item(Safe(), "listening").Status);
        var apps = Item(Safe(b => { b.Listeners!.Add(new("steam", 27036, IPAddress.Any)); b.Listeners.Add(new("Spotify", 57621, IPAddress.Any)); }), "listening");
        Assert.Equal(SecurityStatus.Info, apps.Status);
        Assert.Contains("steam (27036)", apps.Detail);
        Assert.DoesNotContain("svchost", apps.Detail);
    }

    [Fact]
    public void ADnsThatAnswersForMadeUpNames_IsWorthALook()
    {
        var item = Item(Safe(b => b.MadeUp = new(DnsOutcome.Ok, new[] { IPAddress.Parse("92.242.132.24") }, 8)), "nxdomain");
        Assert.Equal(SecurityStatus.Fair, item.Status);
        Assert.Contains("92.242.132.24", item.Detail);
        Assert.Equal(SecurityStatus.Good, Item(Safe(), "nxdomain").Status);
    }

    [Fact]
    public void SomeoneElsesCertificate_IsAProblem_ALocalRootIsInspection()
    {
        var wrong = Safe(b => b.Tls![0] = new TlsObservation("www.google.com", true, false, "Fake Root", false, "RemoteCertificateChainErrors"));
        Assert.Equal(SecurityStatus.Poor, Item(wrong, "dnsreal").Status);
        var inspected = Safe(b => b.Tls = SecurityCheckService.TestSites.Select(s => new TlsObservation(s, true, true, "Kaspersky Anti-Virus Personal Root Certificate", false)).ToList());
        var item = Item(inspected, "inspection");
        Assert.Equal(SecurityStatus.Fair, item.Status);
        Assert.Contains("Kaspersky Anti-Virus Personal Root Certificate", item.Detail);
        Assert.Equal(SecurityStatus.Good, Item(Safe(), "inspection").Status);
    }

    [Fact]
    public void BeforeTheNetworkCheck_ItsLinesSayHowToRunIt()
    {
        var r = SecurityCheck.Evaluate(Safe(b => { b.MadeUp = null; b.MadeUpPublic = null; b.Tls = null; }), T0);
        Assert.All(r.Items.Where(i => i.Id is "nxdomain" or "dnsreal" or "inspection" or "upnp"), i => Assert.Equal(SecurityStatus.Unknown, i.Status));
        Assert.Contains("Press Check", r.Headline);
    }

    private sealed class FakeProbes : ISecurityProbes
    {
        public readonly List<string> Looked = new();
        public readonly List<IPAddress?> Tls = new();
        public FirewallState? Firewall() => new(FirewallState.Public, true, true, true);
        public IReadOnlyList<Listener>? Listeners() => Array.Empty<Listener>();
        public Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct)
        {
            Looked.Add(host);
            return Task.FromResult(host.StartsWith("lagwise-check-", StringComparison.Ordinal)
                ? new DnsAnswer(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 3)
                : new DnsAnswer(DnsOutcome.Ok, new[] { IPAddress.Parse("0.0.0.0"), IPAddress.Parse("2a00:1450::1"), IPAddress.Parse("142.250.1.1") }, 3));
        }
        public Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct) => Task.FromResult(new DnsAnswer(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 3));
        public Task<TlsObservation> TlsAsync(string host, IPAddress? address, CancellationToken ct)
        {
            Tls.Add(address);
            return Task.FromResult(new TlsObservation(host, true, true, "Root", true));
        }
        public Task<bool?> UpnpAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
    }

    [Fact]
    public async Task TheNetworkCheck_UsesAMadeUpName_AndTheFirstRealIPv4Address()
    {
        var probes = new FakeProbes();
        var service = new SecurityCheckService(() => new SecurityCheckService.NetworkInterfaceInfoView(false, null, null), probes);
        var e = await service.FullAsync(CancellationToken.None);
        string madeUp = probes.Looked.Single(h => h.StartsWith("lagwise-check-", StringComparison.Ordinal));
        Assert.EndsWith(".com", madeUp);
        Assert.True(madeUp.Length < 64);
        Assert.All(probes.Tls, a => Assert.Equal(IPAddress.Parse("142.250.1.1"), a));
        Assert.Equal(SecurityStatus.Good, SecurityCheck.Evaluate(e, T0).Overall);
    }

    // ---- reports without home addresses

    [Fact]
    public void Reports_HideHomeAndIPv6Addresses_ButKeepPublicServersAndTimes()
    {
        string text = "Router 192.168.68.1, PC 10.0.0.12, CGNAT 100.72.3.4, link fe80::1c2b:3a%12, home 2a01:cb00:1234:5678::5, " +
                      "Cloudflare 1.1.1.1, hop 62.115.12.9, at 2026-10-14 20:15:00 and 21:47:45, version 0.16.0.";
        string hidden = ReportPrivacy.HideAddresses(text);
        Assert.DoesNotContain("192.168", hidden);
        Assert.DoesNotContain("10.0.0.12", hidden);
        Assert.DoesNotContain("100.72", hidden);
        Assert.DoesNotContain("fe80", hidden);
        Assert.DoesNotContain("2a01", hidden);
        Assert.Contains("1.1.1.1", hidden);
        Assert.Contains("62.115.12.9", hidden);
        Assert.Contains("20:15:00", hidden);
        Assert.Contains("21:47:45", hidden);
        Assert.Contains("0.16.0", hidden);
        Assert.Equal(5, hidden.Split(ReportPrivacy.Hidden).Length - 1);
    }

    [Fact]
    public async Task TheConnectionReport_HidesAddresses_UnlessAskedNotTo()
    {
        var nic = new NetworkInterfaceInfo { Name = "Ethernet", Ipv4Address = "192.168.68.20", Ipv4Gateway = "192.168.68.1", Ipv6Address = "2a01:cb00:1234:5678::20", DnsServers = new() { "192.168.68.1" } };
        string hidden = await new StorageService().GenerateDiagnosticReportAsync(nic, new DiagnosticResult(), Array.Empty<TargetMetrics>(), new List<ConnectionEvent>(), includeAddresses: false);
        Assert.DoesNotContain("192.168.68", hidden);
        Assert.DoesNotContain("2a01", hidden);
        Assert.Contains("read [hidden]", hidden);
        string full = await new StorageService().GenerateDiagnosticReportAsync(nic, new DiagnosticResult(), Array.Empty<TargetMetrics>(), new List<ConnectionEvent>(), includeAddresses: true);
        Assert.Contains("192.168.68.20", full);
    }

    // ---- hardening helpers

    [Fact]
    public void WindowsTools_AreStartedByFullPath()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "netsh.exe"), SystemTools.Netsh);
        Assert.True(Path.IsPathFullyQualified(SystemTools.Explorer));
        Assert.True(Path.IsPathFullyQualified(SystemTools.Cmd));
    }

    [Theory]
    [InlineData("https://status.riotgames.com/", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData("", false)]
    public void OnlyWebAddresses_AreOpenedInTheBrowser(string url, bool allowed) => Assert.Equal(allowed, SystemTools.IsWebLink(url));

    [Theory]
    [InlineData(@"C:\Users\Me\AppData\Local\Lagwise\report.md", true)]
    [InlineData("C:\\x\\a\" & calc & \".md", false)]
    [InlineData(@"C:\x\100%.md", false)]
    [InlineData(@"relative\report.md", false)]
    public void OnlyPlainPaths_GoIntoACommandLine(string path, bool safe) => Assert.Equal(safe, SystemTools.IsSafeCommandLinePath(path));
}
