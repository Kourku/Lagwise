using NetPulse.Core;
using NetPulse.Core.Models;

namespace NetPulse.Tests;

public class LexiconTests
{
    [Fact]
    public void Glossary_HasUniqueKeysAndCompleteText()
    {
        Assert.Equal(Lexicon.Glossary.Count, Lexicon.Glossary.Select(g => g.Key).Distinct().Count());
        foreach (var g in Lexicon.Glossary)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Term), g.Key);
            Assert.False(string.IsNullOrWhiteSpace(g.Plain), g.Key);
            Assert.False(string.IsNullOrWhiteSpace(g.Detail), g.Key);
            Assert.True(g.Plain.Length < 260, $"{g.Key}: the plain-language text should stay short");
        }
    }

    [Theory]
    [InlineData("latency")]
    [InlineData("variation")]
    [InlineData("loss")]
    [InlineData("router")]
    [InlineData("reference")]
    [InlineData("games")]
    [InlineData("udp")]
    [InlineData("bufferbloat")]
    [InlineData("verdict")]
    public void EveryTermTheUiUses_IsDefined(string key) => Assert.NotNull(Lexicon.Find(key));

    [Fact]
    public void Tip_AddsDetailOnlyInAdvancedMode()
    {
        var plain = Lexicon.Tip("loss", advanced: false);
        var advanced = Lexicon.Tip("loss", advanced: true);
        Assert.StartsWith(plain, advanced);
        Assert.True(advanced.Length > plain.Length);
        Assert.Equal(string.Empty, Lexicon.Tip("no-such-term"));
    }

    [Fact]
    public void Definitions_NeverClaimToMeasureGameServerPingOrBlameAHop()
    {
        var reference = Lexicon.Find("reference")!;
        Assert.Contains("NOT your game's server", reference.Plain);
        Assert.Contains("not blame", Lexicon.Find("route")!.Detail);
        Assert.Contains("never in-game latency", reference.Detail);
    }

    [Fact]
    public void EveryEventTypeTheAppLogs_HasAHumanLabel()
    {
        // event types logged by the engine, monitor, view model and services
        string[] logged =
        {
            "LatencySpike", "PacketLoss", "GatewayDegraded", "DnsFailure", "RouteChange", "InterfaceChanged", "Disconnected", "Restored",
            "GameProcess", "ConnectionChange", "EndpointMeasurement", "GameEndpointUnreachable", "LagMarker", "MonitoringGap", "GameMonitor",
            "BufferbloatTest", "SpeedTest", "EngineError"
        };
        foreach (var t in logged)
        {
            Assert.Contains(t, Lexicon.KnownEventTypes);
            Assert.NotEqual(t, Lexicon.EventLabel(t)); // a label, not the internal identifier
        }
    }

    [Fact]
    public void UnknownEventTypes_FallBackToReadableWords()
    {
        Assert.Equal("Some new thing", Lexicon.EventLabel("SomeNewThing"));
        Assert.Equal(string.Empty, Lexicon.EventLabel(null));
    }

    [Fact]
    public void EveryDiagnosticCategory_HasAVerdictLabel_AndNoneSaysInconclusive()
    {
        foreach (var c in Enum.GetValues<DiagnosticCategory>())
        {
            var label = Lexicon.CategoryLabel(c);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(c.ToString(), label);
            Assert.DoesNotContain("Inconclusive", label);
        }
    }

    [Fact]
    public void SeverityLabels_AreFriendly()
    {
        Assert.Equal("Problem", Lexicon.SeverityLabel("Critical"));
        Assert.Equal("Warning", Lexicon.SeverityLabel("Warning"));
        Assert.Equal("Info", Lexicon.SeverityLabel("Info"));
    }
}
