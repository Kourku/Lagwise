using NetPulse.Core.Games;

namespace NetPulse.Tests;

public class GameProfileTests
{
    private static readonly string[] ExpectedIds = { "lol", "fortnite", "apex", "cs2", "wow", "marvelrivals", "poe" };

    [Fact]
    public void AllSevenTargetGamesHaveProfiles()
    {
        Assert.Equal(7, GameProfiles.All.Count);
        foreach (var id in ExpectedIds) Assert.NotNull(GameProfiles.Find(id));
        Assert.Equal(GameProfiles.All.Count, GameProfiles.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void NoProfileIsSupportedWithoutLiveValidation()
    {
        foreach (var p in GameProfiles.All)
        {
            Assert.NotEqual(ValidationLevel.LiveValidated, p.Validation);
            Assert.Equal(SupportStatus.ReferenceOnly, p.Status);
        }
    }

    [Fact]
    public void LeagueOfLegends_IsNotPromotedOnProcessDetectionAlone()
    {
        var lol = GameProfiles.Find("lol")!;
        Assert.Equal(SupportStatus.ReferenceOnly, lol.Status);
        Assert.True(lol.ProcessNamesVerified);
        Assert.Contains("Not yet validated during a real match", lol.ValidationNotes);
    }

    [Fact]
    public void SupportedStatusIsDerivedFromValidation_NotAssignedByHand()
    {
        var lol = GameProfiles.Find("lol")!;
        var validated = new GameProfile
        {
            Id = "x", DisplayName = "X", Publisher = "p", ProcessRules = lol.ProcessRules, Capabilities = lol.Capabilities,
            StatusPage = lol.StatusPage, Limitations = lol.Limitations, Validation = ValidationLevel.LiveValidated
        };
        Assert.Equal(SupportStatus.Supported, validated.Status);

        var noGameProcess = new GameProfile
        {
            Id = "y", DisplayName = "Y", Publisher = "p", ProcessRules = new[] { new ProcessRule("launcher", ProcessRole.Launcher) },
            Capabilities = lol.Capabilities, StatusPage = lol.StatusPage, Limitations = lol.Limitations, Validation = ValidationLevel.LiveValidated
        };
        Assert.Equal(SupportStatus.NotImplemented, noGameProcess.Status);
    }

    [Fact]
    public void EveryProfileDeclaresGameProcessLimitationsAndHonestCapabilities()
    {
        foreach (var p in GameProfiles.All)
        {
            Assert.Contains(p.ProcessRules, r => r.Role == ProcessRole.Game);
            Assert.NotEmpty(p.Limitations);
            Assert.Contains(p.Limitations, l => l.Contains("UDP"));
            Assert.Contains(p.Limitations, l => l.Contains("Unknown role") || l.Contains("role"));
            Assert.False(p.Capabilities.MatchServerIdentification, $"{p.DisplayName} must not claim match-server identification");
        }
    }

    [Fact]
    public void NoInventedEndpoints()
    {
        Assert.All(GameProfiles.All, p => Assert.Empty(p.VerifiedEndpoints));
    }

    [Fact]
    public void StatusPages_AreOnlyMarkedOfficialWhenAUrlExists()
    {
        foreach (var p in GameProfiles.All)
        {
            if (p.StatusPage.Url == null) Assert.False(p.StatusPage.IsOfficial);
            else Assert.StartsWith("https://", p.StatusPage.Url);
            Assert.False(string.IsNullOrWhiteSpace(p.StatusPage.Note));
        }
        Assert.Equal("https://status.riotgames.com/", GameProfiles.Find("lol")!.StatusPage.Url);
    }

    [Fact]
    public void OnlyLeagueProcessNamesAreMarkedVerified()
    {
        foreach (var p in GameProfiles.All.Where(p => p.Id != "lol")) Assert.False(p.ProcessNamesVerified, p.DisplayName);
    }
}
