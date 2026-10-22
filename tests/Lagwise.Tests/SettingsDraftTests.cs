using Lagwise.Core.Models;

namespace Lagwise.Tests;

public class SettingsDraftTests
{
    private static SettingsDraft Draft() => SettingsDraft.FromSettings(AppSettings.CreateDefault(), false);

    [Fact]
    public void Draft_ReflectsSettings_AndMarksShippedTargetsBuiltIn()
    {
        var d = Draft();
        Assert.Equal(1000, d.ProbeIntervalMs);
        Assert.Contains(d.Targets, t => t.IsGateway && t.IsBuiltIn);
        Assert.All(d.Targets, t => Assert.True(t.IsBuiltIn));
    }

    [Fact]
    public void ApplyTo_ChangesSettings_AndKeepsTheGatewayObjectTheEngineMutates()
    {
        var settings = AppSettings.CreateDefault();
        var gateway = settings.Targets.Single(t => t.IsGateway);
        gateway.Host = "192.168.7.1"; // discovered by the engine

        var d = SettingsDraft.FromSettings(settings, false);
        d.ProbeIntervalMs = 2000;
        d.LatencySpikeThresholdMs = 250;
        d.RecentWindowSeconds = 120;
        d.Targets.First(t => t.Id == "google-dns").Enabled = false;
        d.ApplyTo(settings);

        Assert.Equal(2000, settings.MonitoringIntervalMs);
        Assert.Equal(250, settings.LatencySpikeNotifyThreshold);
        Assert.Equal(120, settings.RecentWindowSeconds);
        Assert.Same(gateway, settings.Targets.Single(t => t.IsGateway));
        Assert.Equal("192.168.7.1", gateway.Host);
        Assert.False(settings.Targets.Single(t => t.Id == "google-dns").IsEnabled);
    }

    [Fact]
    public void ApplyTo_AddsAndRemovesUserTargets_ButShippedTargetsKeepTheirAddress()
    {
        var settings = AppSettings.CreateDefault();
        var d = SettingsDraft.FromSettings(settings, false);
        d.Targets.First(t => t.Id == "cf-dns").Host = "6.6.6.6"; // edits to built-ins are ignored
        d.Targets.Add(new TargetDraft { Name = "My server", Host = "203.0.113.5", Protocol = NetworkProtocol.Tcp, Port = 8080 });
        d.ApplyTo(settings);

        Assert.Equal("1.1.1.1", settings.Targets.Single(t => t.Id == "cf-dns").Host);
        var added = settings.Targets.Single(t => t.Name == "My server");
        Assert.Equal(8080, added.Port);
        Assert.Equal(NetworkProtocol.Tcp, added.Protocol);

        var d2 = SettingsDraft.FromSettings(settings, false);
        d2.Targets.RemoveAll(t => t.Name == "My server");
        d2.ApplyTo(settings);
        Assert.DoesNotContain(settings.Targets, t => t.Name == "My server");
    }

    [Fact]
    public void Validate_RejectsOutOfRangeThresholds_AndNoReferenceServers()
    {
        var d = Draft();
        d.LatencySpikeThresholdMs = 1;
        d.PacketLossThresholdPercent = 500;
        foreach (var t in d.Targets.Where(t => !t.IsGateway && !t.IsDnsResolver)) t.Enabled = false;

        var errors = d.Validate();
        Assert.Equal(3, errors.Count);
        Assert.Empty(Draft().Validate());
    }

    [Theory]
    [InlineData("", "1.1.1.1", "", "name")]
    [InlineData("x", "", "", "address")]
    [InlineData("x", "http://example.com", "", "without http")]
    [InlineData("x", "exa mple.com", "", "without http")]
    [InlineData("x", "not a host!", "", "without http")]
    [InlineData("x", "bad..com", "", "valid")]
    [InlineData("x", "example.com", "99999", "port")]
    [InlineData("x", "example.com", "abc", "port")]
    public void TargetValidator_RejectsBadInput(string name, string host, string port, string messagePart)
    {
        var error = TargetValidator.Validate(name, host, NetworkProtocol.Tcp, port, new List<TargetDraft>(), out _);
        Assert.NotNull(error);
        Assert.Contains(messagePart, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetValidator_AcceptsGoodInput_DefaultsPortTo443_AndRejectsDuplicates()
    {
        var existing = new List<TargetDraft>();
        Assert.Null(TargetValidator.Validate("Mine", "example.com", NetworkProtocol.Https, "", existing, out var port));
        Assert.Equal(443, port);
        Assert.Null(TargetValidator.Validate("Mine", "2001:db8::1", NetworkProtocol.Icmp, "", existing, out port));
        Assert.Equal(0, port);

        existing.Add(new TargetDraft { Name = "Mine", Host = "example.com", Protocol = NetworkProtocol.Https, Port = 443 });
        Assert.Contains("already", TargetValidator.Validate("Again", "EXAMPLE.com", NetworkProtocol.Https, "443", existing, out _));
    }

    [Fact]
    public void TargetValidator_CapsTheNumberOfTargets()
    {
        var existing = Enumerable.Range(0, TargetValidator.MaxTargets).Select(i => new TargetDraft { Host = $"10.0.0.{i}" }).ToList();
        Assert.Contains("at most", TargetValidator.Validate("n", "example.org", NetworkProtocol.Icmp, "", existing, out _));
    }
}
