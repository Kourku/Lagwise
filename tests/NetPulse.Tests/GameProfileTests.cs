using NetPulse.Core.Games;

namespace NetPulse.Tests;

public class GameProfileTests
{
    [Fact]
    public void TheSevenBuiltInGamesAreKnown()
    {
        var ids = GameProfiles.All.Select(p => p.Id).ToList();
        Assert.Equal(new[] { "lol", "fortnite", "apex", "cs2", "wow", "marvelrivals", "poe" }, ids);
        Assert.All(GameProfiles.All, p => Assert.Contains(p.ProcessRules, r => r.Role == ProcessRole.Game));
        Assert.All(GameProfiles.All, p => Assert.Equal(GameSource.BuiltIn, p.Source));
    }

    [Fact]
    public void ApexIsFoundByItsDirectX12Program()
    {
        // r5apex_dx12.exe is what Windows' game list showed on a real PC
        Assert.Contains(GameProfiles.Find("apex")!.ProcessRules, r => r.ExecutableName == "r5apex_dx12" && r.Role == ProcessRole.Game);
    }

    [Fact]
    public void SharedStoreClients_AreNeverPartOfAGame()
    {
        // they run whether or not a given game is played, so "the client is open" would be wrong for every game
        var shared = new[] { "steam", "steamwebhelper", "EpicGamesLauncher", "EADesktop", "EABackgroundService", "Battle.net", "Agent", "RiotClientServices", "Riot Client" };
        foreach (var p in GameProfiles.All)
            Assert.DoesNotContain(p.ProcessRules, r => shared.Contains(r.ExecutableName, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void StatusPages_AreOfficialHttpsPages()
    {
        foreach (var p in GameProfiles.All.Where(p => p.HasStatusPage))
        {
            Assert.True(p.StatusPage.IsOfficial);
            Assert.StartsWith("https://", p.StatusPage.Url);
        }
        Assert.Equal(new[] { "lol", "fortnite", "apex" }, GameProfiles.All.Where(p => p.HasStatusPage).Select(p => p.Id));
    }

    [Fact]
    public void NoProfileCarriesServerAddresses()
    {
        // NetPulse never claims to know a game's servers: no profile property holds an address
        Assert.DoesNotContain(typeof(GameProfile).GetProperties(), p => p.Name.Contains("Endpoint") || p.Name.Contains("Server"));
    }
}
