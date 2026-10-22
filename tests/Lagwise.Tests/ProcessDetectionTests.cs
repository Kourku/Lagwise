using Lagwise.Core.Games;

namespace Lagwise.Tests;

public class ProcessDetectionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GameProfile Lol = GameProfiles.Find("lol")!;

    private const string GamePath = @"E:\Riot Games\League of Legends\Game\League of Legends.exe";
    private const string ClientPath = @"E:\Riot Games\League of Legends\LeagueClient.exe";

    private static ProcessInfo P(int pid, string name, string? path = null, int startOffset = 0) =>
        new(pid, name, T0.AddSeconds(startOffset), path);

    [Fact]
    public void NoRelatedProcesses_IsNotRunning()
    {
        var s = GameProcessDetector.Detect(Lol, new[] { P(1, "chrome"), P(2, "explorer") });
        Assert.Equal(GameRunState.NotRunning, s.State);
        Assert.Empty(s.GameProcesses);
    }

    [Fact]
    public void ClientAlone_IsNotTheGame()
    {
        var s = GameProcessDetector.Detect(Lol, new[] { P(13, "LeagueClient", ClientPath), P(14, "LeagueClientUx", ClientPath) });

        Assert.Equal(GameRunState.LauncherOnly, s.State);
        Assert.Empty(s.GameProcesses);
        Assert.Equal(2, s.CompanionProcesses.Count);
        Assert.Contains("client is open", s.Message);
    }

    [Fact]
    public void CrashReporterOrHelperAlone_IsNeverTreatedAsTheGame()
    {
        var s = GameProcessDetector.Detect(Lol, new[] { P(1, "LeagueCrashHandler64"), P(2, "LeagueClientUxRender", ClientPath) });
        Assert.Equal(GameRunState.NotRunning, s.State);
        Assert.Empty(s.GameProcesses);
    }

    [Fact]
    public void GameProcess_IsDetected_WithCompanionsSeparated()
    {
        var s = GameProcessDetector.Detect(Lol, new[]
        {
            P(5, "LeagueClient", ClientPath),
            P(6, "League of Legends", GamePath, 10),
            P(7, "LeagueCrashHandler64"),
        });

        Assert.Equal(GameRunState.Running, s.State);
        Assert.Equal(6, Assert.Single(s.GameProcesses).Pid);
        Assert.Equal(2, s.CompanionProcesses.Count);
        Assert.Equal("League of Legends is running.", s.Message);
    }

    [Fact]
    public void ExeSuffixAndCaseAreIgnored()
    {
        var s = GameProcessDetector.Detect(Lol, new[] { new ProcessInfo(9, "LEAGUE OF LEGENDS.EXE", T0, GamePath) });
        Assert.Equal(GameRunState.Running, s.State);
    }

    [Fact]
    public void SameNameInTheWrongFolder_IsReportedUncertain_NotClaimedAsTheGame()
    {
        var s = GameProcessDetector.Detect(Lol, new[] { P(9, "League of Legends", @"C:\Users\x\Downloads\totally-different\League of Legends.exe".Replace("League of Legends.exe", "lol.exe")) });
        // path does not contain the install-folder hint, but the name matches
        Assert.Equal(GameRunState.Uncertain, s.State);
        Assert.Empty(s.GameProcesses);
        Assert.Single(s.UnconfirmedProcesses);
        Assert.Contains("another folder", s.Message);
    }

    [Fact]
    public void UnreadablePath_OfABuiltInGame_StillDetectsByName()
    {
        // a protected game process can hide its location; a built-in game's own name is then enough
        var s = GameProcessDetector.Detect(Lol, new[] { P(9, "League of Legends", null) });
        Assert.Equal(GameRunState.Running, s.State);
        Assert.False(Assert.Single(s.GameProcesses).PathConfirmed);
    }

    // ---- games known by their exact file (Windows' game list, or added by the user) ----

    private static GameProfile ByPath(string path) => new()
    {
        Id = "user:test", DisplayName = "Test Game", Source = GameSource.AddedByYou, ExecutablePath = path,
        ProcessRules = new[] { new ProcessRule(Path.GetFileNameWithoutExtension(path), ProcessRole.Game, ExactPath: path) }
    };

    [Fact]
    public void ExactPathGame_IsDetectedOnlyAtThatLocation()
    {
        var g = ByPath(@"D:\Games\Some Game\Game.exe");

        Assert.Equal(GameRunState.Running, GameProcessDetector.Detect(g, new[] { P(1, "Game", @"d:\games\some game\GAME.EXE") }).State);
        // another program that happens to be called Game.exe is a different program, not a look-alike
        var other = GameProcessDetector.Detect(g, new[] { P(2, "Game", @"C:\Tools\Other\Game.exe") });
        Assert.Equal(GameRunState.NotRunning, other.State);
        Assert.Empty(other.UnconfirmedProcesses);
    }

    [Fact]
    public void ExactPathGame_WithAnUnreadableLocation_IsNotClaimed()
    {
        var s = GameProcessDetector.Detect(ByPath(@"D:\Games\Some Game\Game.exe"), new[] { P(1, "Game", null) });
        Assert.Equal(GameRunState.Uncertain, s.State);
        Assert.Contains("does not say where", s.Message);
    }

    [Fact]
    public void MultipleGameProcesses_AreAllTracked_AndOldestIsPrimary()
    {
        var wow = GameProfiles.Find("wow")!;
        var s = GameProcessDetector.Detect(wow, new[]
        {
            P(20, "Wow", @"D:\Games\World of Warcraft\_retail_\Wow.exe", 50),
            P(21, "WowClassic", @"D:\Games\World of Warcraft\_classic_\WowClassic.exe", 5),
            P(22, "Battle.net", null),
        });

        Assert.Equal(GameRunState.Running, s.State);
        Assert.Equal(2, s.GameProcesses.Count);
        Assert.Equal(21, s.Primary!.Pid);
        Assert.Empty(s.CompanionProcesses);
    }

    [Fact]
    public void SharedStoreClients_AreNoGamesLauncher()
    {
        // Steam, the Epic Games Launcher, the EA app and Battle.net run whether or not a given game is played
        foreach (var p in GameProfiles.All)
        {
            var s = GameProcessDetector.Detect(p, new[] { P(1, "steam"), P(2, "EpicGamesLauncher"), P(3, "EADesktop"), P(4, "Battle.net"), P(5, "steamwebhelper") });
            Assert.Equal(GameRunState.NotRunning, s.State);
        }
    }

    // ---- watcher: start / exit / restart / PID reuse ----

    [Fact]
    public void Watcher_ReportsStartAndExit_Once()
    {
        var w = new GameWatcher(Lol);

        Assert.Empty(w.Observe(new[] { P(1, "chrome") }, T0).Changes);

        var started = w.Observe(new[] { P(6, "League of Legends", GamePath) }, T0.AddSeconds(2));
        Assert.Equal(GameProcessChangeKind.GameStarted, Assert.Single(started.Changes).Kind);

        Assert.Empty(w.Observe(new[] { P(6, "League of Legends", GamePath) }, T0.AddSeconds(4)).Changes);

        var exited = w.Observe(Array.Empty<ProcessInfo>(), T0.AddSeconds(6));
        Assert.Equal(GameProcessChangeKind.GameExited, Assert.Single(exited.Changes).Kind);
        Assert.Equal(1, w.LaunchCount);
    }

    [Fact]
    public void Watcher_HandlesRestartWithNewProcessId()
    {
        var w = new GameWatcher(Lol);
        w.Observe(new[] { P(6, "League of Legends", GamePath) }, T0);
        w.Observe(Array.Empty<ProcessInfo>(), T0.AddSeconds(2));
        var again = w.Observe(new[] { P(77, "League of Legends", GamePath, 100) }, T0.AddSeconds(4));

        Assert.Equal(GameProcessChangeKind.GameStarted, Assert.Single(again.Changes).Kind);
        Assert.Equal(2, w.LaunchCount);
        Assert.Equal(77, again.State.Primary!.Pid);
    }

    [Fact]
    public void Watcher_DetectsInstanceChangeWithoutAnExitGap()
    {
        var w = new GameWatcher(Lol);
        w.Observe(new[] { P(6, "League of Legends", GamePath) }, T0);

        // restarted between two polls: never observed as "not running"
        var update = w.Observe(new[] { P(99, "League of Legends", GamePath, 60) }, T0.AddSeconds(2));

        Assert.Equal(GameProcessChangeKind.InstancesChanged, Assert.Single(update.Changes).Kind);
        Assert.Equal(1, w.LaunchCount);
    }

    [Fact]
    public void Watcher_TreatsRecycledPidWithNewStartTimeAsANewProcess()
    {
        var w = new GameWatcher(Lol);
        w.Observe(new[] { P(6, "League of Legends", GamePath, 0) }, T0);
        var update = w.Observe(new[] { P(6, "League of Legends", GamePath, 500) }, T0.AddSeconds(2));

        Assert.Equal(GameProcessChangeKind.InstancesChanged, Assert.Single(update.Changes).Kind);
    }

    [Fact]
    public void Watcher_LauncherComingAndGoing_DoesNotProduceGameEvents()
    {
        var w = new GameWatcher(Lol);
        Assert.Empty(w.Observe(new[] { P(1, "LeagueClient", ClientPath) }, T0).Changes);
        Assert.Empty(w.Observe(Array.Empty<ProcessInfo>(), T0.AddSeconds(2)).Changes);
    }

    [Fact]
    public void SystemProcessSource_ListsRealProcessesIncludingThisOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = new SystemProcessSource(new[] { System.Diagnostics.Process.GetCurrentProcess().ProcessName });
        var list = source.GetProcesses();

        var me = list.Single(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(me.StartTimeUtc);
        Assert.False(string.IsNullOrEmpty(me.ExecutablePath));
        Assert.True(list.Count > 10);
    }

    [Fact]
    public void RealSource_DoesNotMistakeUnrelatedProcessesForTheGame()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = new SystemProcessSource(GameProfiles.AllProcessNames);
        var state = GameProcessDetector.Detect(Lol, source.GetProcesses());

        // whatever the machine is doing, the detector must be self-consistent
        if (state.State == GameRunState.Running) Assert.NotEmpty(state.GameProcesses);
        else Assert.Empty(state.GameProcesses);
    }
}
