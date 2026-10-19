using System.Net;
using NetPulse.Core.Sites;

namespace NetPulse.Tests;

/// <summary>Site check: is it down, blocked by your DNS, blocked on the way, intercepted? (0.15)</summary>
public class SiteCheckTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress Real = IPAddress.Parse("151.101.1.140");
    private static readonly IPAddress Other = IPAddress.Parse("203.0.113.9");

    private sealed class FakeProbes : ISiteProbes
    {
        public string? Hosts;
        public IPAddress? HostsIp;
        public DnsAnswer System = Ok(Real);
        public DnsAnswer Public = Ok(Real);
        public Dictionary<IPAddress, ConnectionTrace> Visits = new();
        public bool OtherNameAnswers = true;
        public StatusPageAnswer? Status;
        public List<string> Calls = new();

        public static DnsAnswer Ok(params IPAddress[] a) => new(DnsOutcome.Ok, a, 12, Resolver: "Cloudflare DNS (1.1.1.1)");

        public string? HostsEntry(string host, out IPAddress? address) { address = HostsIp; return Hosts; }
        public Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct) => Task.FromResult(System);
        public Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct) => Task.FromResult(Public);
        public Task<ConnectionTrace> VisitAsync(IPAddress address, string host, CancellationToken ct)
        {
            Calls.Add("visit " + address);
            return Task.FromResult(Visits.TryGetValue(address, out var t) ? t : Up(address));
        }
        public Task<bool> HandshakeAnswersAsync(IPAddress address, string otherName, CancellationToken ct)
        {
            Calls.Add("control " + otherName);
            return Task.FromResult(OtherNameAnswers);
        }
        public Task<StatusPageAnswer> StatusAsync(string url, CancellationToken ct) =>
            Status != null ? Task.FromResult(Status) : Task.FromException<StatusPageAnswer>(new HttpRequestException("offline"));
    }

    private static ConnectionTrace Up(IPAddress a, int code = 200, double ms = 150, bool challenge = false) =>
        new(a, TraceStage.Done, TraceFailure.None, 20, 40, ms, code, Challenge: challenge);

    private static ConnectionTrace Fail(IPAddress a, TraceStage at, TraceFailure f) => new(a, at, f, at > TraceStage.Connect ? 20 : null);

    private static readonly SiteDefinition Site = new("reddit", "Reddit", "www.reddit.com", "Social", "https://www.redditstatus.com/api/v2/status.json");

    private static async Task<SiteResult> Check(FakeProbes p, SiteDefinition? site = null, bool othersUp = true)
    {
        var service = new SiteCheckService(p, () => T0);
        var target = site ?? Site;
        if (!othersUp) return (await service.CheckAsync(new[] { target })).Single();
        // a second site that always answers, so "others reachable" is known
        var e = await service.GatherAsync(target, CancellationToken.None);
        return SiteDiagnosis.Diagnose(target, e, T0, othersReachable: true);
    }

    [Fact]
    public async Task ASiteThatAnswers_IsUp()
    {
        var r = await Check(new FakeProbes { Status = new("none", "All Systems Operational") });
        Assert.Equal(SiteVerdict.Up, r.Verdict);
        Assert.Contains("answered in 150 ms", r.Explanation);
        Assert.Contains(r.Steps, s => s.Title == "Status page" && s.Detail.Contains("All Systems Operational"));
        Assert.True(r.IsReachable);
    }

    [Fact]
    public async Task ADeadEndAddressFromYourDns_WhilePublicDnsWorks_IsADnsBlock()
    {
        var p = new FakeProbes { System = FakeProbes.Ok(IPAddress.Parse("0.0.0.0")) };
        var r = await Check(p);
        Assert.Equal(SiteVerdict.DnsBlocked, r.Verdict);
        Assert.Contains("sends www.reddit.com to 0.0.0.0", r.Explanation);
        Assert.Contains("1.1.1.1", r.Advice);
        Assert.Equal(new[] { "visit " + Real }, p.Calls);   // the real address was tried, and worked
        Assert.True(r.IsBlocked);
    }

    [Fact]
    public async Task ANameYourDnsDoesNotKnow_WhilePublicDnsDoes_IsADnsBlock()
    {
        var r = await Check(new FakeProbes { System = new(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 5) });
        Assert.Equal(SiteVerdict.DnsBlocked, r.Verdict);
        Assert.Contains("could not look up", r.Explanation);
    }

    [Fact]
    public async Task YourDnsSendingYouToAnotherServer_IsADnsBlock()
    {
        // your DNS gives a public address that is not the site's: the visit there shows someone else's certificate
        var p = new FakeProbes { System = FakeProbes.Ok(Other) };
        p.Visits[Other] = new(Other, TraceStage.Handshake, TraceFailure.CertificateInvalid, 20, 30, CertificateSubject: "block.provider.example", CertificateIssuer: "Provider CA");
        var r = await Check(p);
        Assert.Equal(SiteVerdict.DnsBlocked, r.Verdict);
        Assert.Contains("wrong server", r.Explanation);
        Assert.Equal(new[] { "visit " + Other, "visit " + Real }, p.Calls);
    }

    [Fact]
    public async Task SomeoneElsesCertificate_AtTheRealAddress_IsAnInterception()
    {
        var p = new FakeProbes();
        p.Visits[Real] = new(Real, TraceStage.Handshake, TraceFailure.CertificateInvalid, 20, 30, CertificateSubject: "Kaspersky Anti-Virus Personal Root", CertificateIssuer: "Kaspersky");
        var r = await Check(p);
        Assert.Equal(SiteVerdict.Intercepted, r.Verdict);
        Assert.Contains("issued to Kaspersky Anti-Virus Personal Root by Kaspersky", r.Explanation);
    }

    [Fact]
    public async Task AHandshakeCutOnlyWhenItNamesTheSite_IsFilteringByName()
    {
        var p = new FakeProbes();
        p.Visits[Real] = Fail(Real, TraceStage.Handshake, TraceFailure.Reset);
        var r = await Check(p);
        Assert.Equal(SiteVerdict.ProviderBlocked, r.Verdict);
        Assert.Contains("(SNI)", r.Explanation);
        Assert.Contains("control " + SiteCheckService.ControlName, p.Calls);
        Assert.Contains(r.Steps, s => s.Title == "Control handshake");

        p.OtherNameAnswers = false;   // the server does not answer anything: not a name filter
        Assert.Equal(SiteVerdict.Unreachable, (await Check(p)).Verdict);
    }

    [Fact]
    public async Task NoConnection_WhileOtherSitesWork_CannotBeToldFromDown_ButTheStatusPageHelps()
    {
        var p = new FakeProbes { Status = new("none", "All Systems Operational") };
        p.Visits[Real] = Fail(Real, TraceStage.Connect, TraceFailure.Timeout);
        var r = await Check(p);
        Assert.Equal(SiteVerdict.Unreachable, r.Verdict);
        Assert.Contains("from one PC the two look the same", r.Explanation);
        Assert.Contains("points at a block between you and the site", r.Explanation);

        p.Status = new("major", "Major Outage");
        var down = await Check(p);
        Assert.Equal(SiteVerdict.SiteProblem, down.Verdict);
        Assert.Contains("down for everyone", down.Explanation);
    }

    [Fact]
    public async Task WhenNothingAnswersAnywhere_ItIsYourConnection()
    {
        var p = new FakeProbes();
        p.Visits[Real] = Fail(Real, TraceStage.Connect, TraceFailure.Timeout);
        var service = new SiteCheckService(p, () => T0);
        var results = await service.CheckAsync(new[] { Site, Site with { Id = "x", Name = "X", Host = "x.com", StatusApi = null } });
        Assert.All(results, r => Assert.Equal(SiteVerdict.Offline, r.Verdict));

        var offline = await new SiteCheckService(new FakeProbes(), () => T0, internetDown: () => true).CheckAsync(new[] { Site });
        Assert.Equal(SiteVerdict.Offline, offline.Single().Verdict);
    }

    [Theory]
    [InlineData(451, false, SiteVerdict.LegalBlock)]
    [InlineData(503, false, SiteVerdict.SiteProblem)]
    [InlineData(503, true, SiteVerdict.Up)]
    [InlineData(403, false, SiteVerdict.Up)]
    [InlineData(301, false, SiteVerdict.Up)]
    public async Task TheAnswer_DecidesBetweenUp_LegalBlock_AndASiteProblem(int code, bool challenge, SiteVerdict expected)
    {
        var p = new FakeProbes();
        p.Visits[Real] = Up(Real, code, challenge: challenge);
        var r = await Check(p);
        Assert.Equal(expected, r.Verdict);
        if (challenge) Assert.Contains("bot check", r.Explanation);
    }

    [Fact]
    public async Task ASlowAnswer_IsSlow()
    {
        var p = new FakeProbes();
        p.Visits[Real] = Up(Real, ms: 4500);
        var r = await Check(p);
        Assert.Equal(SiteVerdict.Slow, r.Verdict);
        Assert.Contains("4.5 s", r.Explanation);
    }

    [Fact]
    public async Task AHostsFileBlock_IsOnThisPc_AndNothingIsVisited()
    {
        var p = new FakeProbes { Hosts = "0.0.0.0 www.reddit.com", HostsIp = IPAddress.Any, System = FakeProbes.Ok(IPAddress.Any) };
        var r = await Check(p);
        Assert.Equal(SiteVerdict.BlockedOnThisPc, r.Verdict);
        Assert.Empty(p.Calls);
        Assert.Contains("hosts", r.Advice);
    }

    [Fact]
    public async Task ANameNobodyKnows_IsNotFound()
    {
        var none = new DnsAnswer(DnsOutcome.NotFound, Array.Empty<IPAddress>(), 5);
        var r = await Check(new FakeProbes { System = none, Public = none });
        Assert.Equal(SiteVerdict.NoSuchSite, r.Verdict);
    }

    [Theory]
    [InlineData("https://Example.COM/some/page", "example.com")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData(" www.youtube.com ", "www.youtube.com")]
    [InlineData("localhost", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("not a site", null)]
    [InlineData("", null)]
    public void HandAddedSites_AreReducedToTheirHost(string input, string? expected) =>
        Assert.Equal(expected, SiteCatalog.NormalizeHost(input));

    [Fact]
    public void ThePopularSites_AreAllPublicHosts_WithAtMostVerifiedStatusPages()
    {
        Assert.Contains(SiteCatalog.Popular, s => s.Id == "youtube");
        Assert.Contains(SiteCatalog.Popular, s => s.Id == "x");
        Assert.Contains(SiteCatalog.Popular, s => s.Id == "reddit");
        Assert.All(SiteCatalog.Popular, s => Assert.Equal(s.Host, SiteCatalog.NormalizeHost(s.Host)));
        Assert.All(SiteCatalog.Popular.Where(s => s.StatusApi != null), s => Assert.EndsWith("/api/v2/status.json", s.StatusApi));
        Assert.Null(SiteCatalog.Popular.Single(s => s.Id == "x").StatusApi);   // X's status page needs a key: not used
        Assert.Equal(SiteCatalog.Popular.Count, SiteCatalog.Popular.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void PublicDnsAnswers_AreRead_IncludingAliasesAndUnknownNames()
    {
        // as Cloudflare and Google answer (shortened)
        var ok = SiteProbes.ParseDnsJson("""{"Status":0,"Answer":[{"name":"www.reddit.com","type":5,"data":"reddit.map.fastly.net."},{"name":"reddit.map.fastly.net","type":1,"data":"151.101.1.140"}]}""", "Cloudflare", 10)!;
        Assert.Equal(DnsOutcome.Ok, ok.Outcome);
        Assert.Equal(Real, Assert.Single(ok.Addresses));
        Assert.Equal(DnsOutcome.NotFound, SiteProbes.ParseDnsJson("""{"Status":3}""", "Google", 10)!.Outcome);
        Assert.Null(SiteProbes.ParseDnsJson("<html>blocked</html>", "x", 1));
    }

    [Fact]
    public void StatusPages_AreRead()
    {
        var s = SiteProbes.ParseStatusJson("""{"page":{"id":"2kbc0d48tv3j","name":"Reddit"},"status":{"indicator":"none","description":"All Systems Operational"}}""")!;
        Assert.Equal("All Systems Operational", s.Description);
        Assert.False(s.ReportsTrouble);
        Assert.True(SiteProbes.ParseStatusJson("""{"status":{"indicator":"major","description":"Partial System Outage"}}""")!.ReportsOutage);
        Assert.Null(SiteProbes.ParseStatusJson("""{"error":"Your page is inactive."}"""));
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.10.34.35", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("151.101.1.140", false)]
    [InlineData("2a04:4e42::396", false)]
    public void DeadEndAddresses_AreRecognised(string ip, bool deadEnd) =>
        Assert.Equal(deadEnd, SiteDiagnosis.IsDeadEndAddress(IPAddress.Parse(ip)));

    [Fact]
    public void Icons_AreOnlyTheFormatsWindowsDraws()
    {
        Assert.Equal(".ico", SiteCheckService.IconExtension(new byte[] { 0, 0, 1, 0, 1, 0, 16, 16 }));
        Assert.Equal(".png", SiteCheckService.IconExtension(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
        Assert.Null(SiteCheckService.IconExtension("<!doctype html>"u8));
        Assert.Null(SiteCheckService.IconExtension("<svg xmlns="u8));
    }
}
