using NetPulse.Core.Games;

namespace NetPulse.Tests;

/// <summary>Finding the player's games on any PC: built-in names, Windows' own game list, and programs added by hand.</summary>
public class GameLibraryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
    private static (string?, string?) NoInfo(string _) => (null, null);

    private static IReadOnlyList<GameProfile> Build(IEnumerable<WindowsGameEntry> windows, IEnumerable<string>? added = null,
        IEnumerable<string>? ignored = null, Func<string, (string?, string?)>? info = null, Func<string, bool>? exists = null) =>
        GameLibrary.Build(GameProfiles.All, windows, added ?? Array.Empty<string>(), ignored, exists ?? (_ => true), info ?? NoInfo);

    [Fact]
    public void ABuiltInGameInWindowsList_GetsItsLocationAndLastPlayed_WithoutADuplicate()
    {
        var lib = Build(new[]
        {
            new WindowsGameEntry(@"F:\Riot\League of Legends\Game\League of Legends.exe", T0, "1811699532"),
            new WindowsGameEntry(@"F:\Riot\League of Legends\LeagueClientUx.exe", T0.AddHours(-1), "1811699532")   // League's client
        });

        var lol = lib.Single(p => p.Id == "lol");
        Assert.Equal(@"F:\Riot\League of Legends\Game\League of Legends.exe", lol.ExecutablePath);
        Assert.Equal(T0, lol.LastPlayedUtc);
        Assert.Equal(GameProfiles.All.Count, lib.Count);    // the client did not become a game of its own
    }

    [Fact]
    public void OtherGamesInWindowsList_BecomeGames_MatchedByTheirExactFile()
    {
        var lib = Build(new[] { new WindowsGameEntry(@"D:\SteamLibrary\steamapps\common\No Man's Sky\Binaries\NMS.exe", T0, "1905450963") },
            info: _ => ("No Man's Sky", "No Man's Sky"));

        var nms = lib.Single(p => p.Source == GameSource.Windows);
        Assert.Equal("win:1905450963", nms.Id);
        Assert.Equal("No Man's Sky", nms.DisplayName);
        var rule = Assert.Single(nms.ProcessRules);
        Assert.Equal("NMS", rule.ExecutableName);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\No Man's Sky\Binaries\NMS.exe", rule.ExactPath);
    }

    [Fact]
    public void ProgramsOfOneTitle_AreOneGame()
    {
        var lib = Build(new[]
        {
            new WindowsGameEntry(@"D:\Games\BeamNG.drive\BeamNG.drive.exe", T0.AddDays(-1), "2058206598"),
            new WindowsGameEntry(@"D:\Games\BeamNG.drive\Bin64\BeamNG.drive.x64.exe", T0, "2058206598")
        });
        var game = Assert.Single(lib, p => p.Source == GameSource.Windows);
        Assert.Equal(2, game.ProcessRules.Count);
        Assert.Equal(@"D:\Games\BeamNG.drive\Bin64\BeamNG.drive.x64.exe", game.ExecutablePath);   // the one played last
        Assert.Equal(T0, game.LastPlayedUtc);
    }

    [Fact]
    public void UninstalledGames_AndUnusableEntries_AreLeftOut()
    {
        var lib = Build(new[]
        {
            new WindowsGameEntry(@"D:\Games\Gone\Gone.exe", T0),
            new WindowsGameEntry(@"relative\path.exe", T0),
            new WindowsGameEntry(@"D:\Games\NotAnExe\readme.txt", T0)
        }, exists: p => !p.Contains("Gone"));
        Assert.DoesNotContain(lib, p => p.Source != GameSource.BuiltIn);
    }

    [Fact]
    public void AddedGames_AreGames_UnlessAlreadyFoundAnotherWay()
    {
        var lib = Build(new[] { new WindowsGameEntry(@"D:\Games\Hades\Hades.exe", T0) },
            added: new[] { @"D:\Games\Hades\Hades.exe", @"E:\Indie\Tiny Game\tiny.exe", @"E:\Indie\Tiny Game\tiny.exe" });

        Assert.Single(lib, p => p.Source == GameSource.Windows);
        var mine = Assert.Single(lib, p => p.Source == GameSource.AddedByYou);
        Assert.Equal("Tiny Game", mine.DisplayName);
        Assert.StartsWith(GameLibrary.UserPrefix, mine.Id);
    }

    [Fact]
    public void GamesTakenOffTheList_AreNotWatched()
    {
        var all = Build(new[] { new WindowsGameEntry(@"D:\Tools\LaunchBox\LaunchBox.exe", T0, "77") });
        var without = Build(new[] { new WindowsGameEntry(@"D:\Tools\LaunchBox\LaunchBox.exe", T0, "77") }, ignored: new[] { "win:77", "cs2" });
        Assert.Equal(all.Count - 2, without.Count);
        Assert.DoesNotContain(without, p => p.Id is "win:77" or "cs2");
    }

    [Fact]
    public void ABuiltInNameInAnotherFolder_IsItsOwnGame()
    {
        // Wow.exe outside a World of Warcraft folder is some other program called Wow
        var lib = Build(new[] { new WindowsGameEntry(@"D:\Games\Wow Racing\Wow.exe", T0) });
        Assert.Null(lib.Single(p => p.Id == "wow").ExecutablePath);
        Assert.Equal("Wow Racing", lib.Single(p => p.Source == GameSource.Windows).DisplayName);
    }

    [Fact]
    public void Ids_AreStableAcrossRuns_AndCaseInsensitive()
    {
        Assert.Equal(GameLibrary.StableId(@"D:\Games\X\x.exe"), GameLibrary.StableId(@"d:\games\x\X.EXE"));
        Assert.NotEqual(GameLibrary.StableId(@"D:\Games\X\x.exe"), GameLibrary.StableId(@"D:\Games\Y\x.exe"));
        Assert.Matches("^[0-9a-f]{12}$", GameLibrary.StableId(@"D:\Games\X\x.exe"));
    }

    [Theory]
    [InlineData(@"D:\Games\Space Marine 2\client_pc\root\bin\pc\SM2 - Retail.exe", null, "Space Marine 2")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Arc Raiders\PioneerGame\Binaries\Win64\PioneerGame.exe", null, "Arc Raiders")]
    [InlineData(@"D:\Games\Dispatch\Dispatch\Binaries\Win64\Dispatch-Win64-Shipping.exe", null, "Dispatch")]
    [InlineData(@"D:\Games\Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe", null, "Hogwarts Legacy")]
    [InlineData(@"D:\Games\RimWorld\RimWorldWin64.exe", null, "RimWorld")]
    [InlineData(@"D:\Emulation\RPCS3\rpcs3.exe", "rpcs3", "RPCS3")]
    [InlineData(@"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077", "Cyberpunk 2077")]
    [InlineData(@"D:\Games\Some Game\Game.exe", "BootstrapPackagedGame", "Some Game")]
    [InlineData(@"D:\Games\X\x.exe", "Grand Theft Auto V™", "Grand Theft Auto V")]
    [InlineData(@"C:\Program Files\speedy_racer.exe", null, "speedy racer")]
    [InlineData(@"D:\Games\Fun\fun.exe", "Bad\u202Egpj.exe", "Badgpj.exe")]
    public void GameNames_ComeFromTheProductName_OrTheGamesFolder(string path, string? product, string expected) =>
        Assert.Equal(expected, GameLibrary.NameFor(path, product));

    [Theory]
    [InlineData(@"D:\Games\a.exe", true)]
    [InlineData(@"\\server\share\game.exe", true)]
    [InlineData(@"game.exe", false)]
    [InlineData(@"D:\Games\a.txt", false)]
    [InlineData(@"D:\Games\.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFullPathsToExeFilesAreUsed(string? path, bool usable) =>
        Assert.Equal(usable, GameLibrary.IsUsableExePath(path));

    [Fact]
    public void WindowsGameList_IsReadOnAnyPc_WithFullPathsOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        // whatever this PC has played: the list reads without failing and holds only full .exe paths
        var entries = new WindowsGameListSource().Read();
        Assert.All(entries, e => Assert.True(GameLibrary.IsUsableExePath(e.ExePath), e.ExePath));
        var lib = GameLibrary.Build(GameProfiles.All, entries, Array.Empty<string>());
        Assert.True(lib.Count >= GameProfiles.All.Count);
        Assert.Equal(lib.Count, lib.Select(p => p.Id).Distinct().Count());
    }
}
