using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lagwise.Core;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;
using Lagwise.Core.Sites;
using Xunit;

namespace Lagwise.Tests;

/// <summary>Lagwise's own safety against hostile answers from the network and hostile text in exports.</summary>
public class AppSecurityTests
{
    // ---- text from the network ----

    [Fact]
    public void Clean_RemovesDirectionOverridesAndInvisibleCharacters()
    {
        Assert.Equal("Systems gpj.exe", UntrustedText.Clean("Systems ‮gpj.exe"));
        Assert.Equal("ab", UntrustedText.Clean("a​b"));
        Assert.Equal("line one line two", UntrustedText.Clean("line one\r\nline two\0"));
    }

    [Fact]
    public void Clean_LimitsTheLength_AndKeepsOrdinaryText()
    {
        Assert.Equal("All Systems Operational 🎮", UntrustedText.Clean("All Systems Operational 🎮"));
        string longText = UntrustedText.Clean(new string('x', 5000), 100)!;
        Assert.Equal(100, longText.Length);
        Assert.EndsWith("…", longText);
        Assert.Null(UntrustedText.Clean(null));
    }

    [Fact]
    public void ReverseDnsName_IsKeptOnlyWhenItIsAHostName()
    {
        Assert.Equal("ae-1.r01.example.net", UntrustedText.HostName("ae-1.r01.example.net."));
        Assert.Equal("", UntrustedText.HostName("=cmd|' /C calc'!A0"));
        Assert.Equal("", UntrustedText.HostName("evil‮moc.example"));
        Assert.Equal("", UntrustedText.HostName("a name with spaces"));
        Assert.Equal("", UntrustedText.HostName(new string('a', 300) + ".com"));
        Assert.Equal("", UntrustedText.HostName(null));
    }

    // ---- CSV exports ----

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("Router \"main\"", "\"Router \"\"main\"\"\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(null, "\"\"")]
    public void CsvField_IsQuoted_AndNeverAFormula(string? value, string expected) =>
        Assert.Equal(expected, UntrustedText.CsvField(value));

    [Fact]
    public void SamplesCsv_NeutralisesAFormulaInATargetName()
    {
        var s = new MonitoringSession { StartedUtc = DateTime.UtcNow };
        s.Targets.Add(new SessionTarget { Id = "t", Name = "=cmd|' /C calc'!A0" });
        s.Samples.Add(new SessionSample { T = DateTime.UtcNow, TargetId = "t", Ok = true, RttMs = 12 });
        string row = SessionReportBuilder.BuildSamplesCsv(s).Split('\n')[1];
        Assert.Contains("\"'=cmd|' /C calc'!A0\"", row);
        Assert.DoesNotContain(",=", row);
    }

    [Fact]
    public async Task MeasurementsCsv_QuotesEveryTextField()
    {
        string path = Path.Combine(Path.GetTempPath(), "np-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await new StorageService().ExportToCsvAsync(path, new[]
            {
                new Lagwise.Core.Models.Measurement { TargetId = "x", TargetName = "@evil", TargetHost = "a,b.example", ErrorMessage = "=1+1" }
            });
            string row = File.ReadAllLines(path)[1];
            Assert.Contains("\"'@evil\"", row);
            Assert.Contains("\"a,b.example\"", row);
            Assert.Contains("\"'=1+1\"", row);
        }
        finally { File.Delete(path); }
    }

    // ---- DNS answers ----

    [Fact]
    public void DnsAnswer_CountsOnlyFromTheServerAsked_ForTheQueryAsked()
    {
        byte[] query = { 0x12, 0x34, 1, 0 };
        var server = IPAddress.Parse("9.9.9.9");
        UdpReceiveResult Reply(string from, int port, byte a, byte b) => new(new byte[] { a, b, 0x81, 0x80 }, new IPEndPoint(IPAddress.Parse(from), port));

        Assert.True(DnsProbeService.IsAnswerTo(query, server, Reply("9.9.9.9", 53, 0x12, 0x34)));
        Assert.True(DnsProbeService.IsAnswerTo(query, server, Reply("::ffff:9.9.9.9", 53, 0x12, 0x34)));
        Assert.False(DnsProbeService.IsAnswerTo(query, server, Reply("192.168.1.66", 53, 0x12, 0x34)));   // someone else on the network
        Assert.False(DnsProbeService.IsAnswerTo(query, server, Reply("9.9.9.9", 53, 0x12, 0x35)));        // another query's answer
        Assert.False(DnsProbeService.IsAnswerTo(query, server, Reply("9.9.9.9", 5353, 0x12, 0x34)));
    }

    // ---- icons from the internet ----

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("142.250.74.36", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.138", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.10.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    public void IconAddresses_OnThisPcOrTheHomeNetwork_AreRefused(string address, bool allowed) =>
        Assert.Equal(allowed, SiteCheckService.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void IconLookup_InACraftedPage_EndsQuickly()
    {
        string page = string.Concat(Enumerable.Repeat("<link rel=\"x\" a=", 4000));
        var sw = Stopwatch.StartNew();
        Assert.Null(SiteProbes.FindIconHref(page));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task IconNamedOnThisPc_IsNeverFetched_TheSitesOwnIconIs()
    {
        // a page naming an icon at 127.0.0.1 (or the router): Lagwise must not even connect there
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        string folder = Path.Combine(Path.GetTempPath(), "np-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var site = new SiteDefinition("wikipedia", "Wikipedia", "www.wikipedia.org", "Reference");
            string? icon = await SiteCheckService.EnsureIconAsync(site, folder, CancellationToken.None, $"https://127.0.0.1:{port}/icon.ico");
            Assert.False(accept.IsCompleted, "Lagwise connected to an address on this PC");
            Assert.NotNull(icon);   // the site's own /favicon.ico, on the internet, still loads (live)
            Assert.True(File.Exists(icon));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // ---- status pages ----

    [Fact]
    public void StatusPage_OfAnotherShape_IsNotAnAnswer_AndItsTextIsCleaned()
    {
        Assert.Null(SiteProbes.ParseStatusJson("""{"status":"ok"}"""));
        Assert.Null(SiteProbes.ParseStatusJson("""{"status":{"indicator":5}}"""));
        var s = SiteProbes.ParseStatusJson("{\"status\":{\"indicator\":\"none\",\"description\":\"All \\u202Egood\\r\\nhere\"}}")!;
        Assert.Equal("All good here", s.Description);
    }
}
