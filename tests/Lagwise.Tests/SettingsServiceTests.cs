using Lagwise.Core.Models;
using Lagwise.Core.Services;

namespace Lagwise.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lagwise-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsService Create() => new(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var s = Create();
        var settings = s.Load();

        Assert.True(settings.IsBeginnerMode);
        Assert.NotEmpty(settings.Targets);
        Assert.Null(s.LastLoadWarning);
    }

    [Fact]
    public void SavedSettings_SurviveReload()
    {
        var s = Create();
        s.Load();
        s.Current.IsBeginnerMode = false;
        s.Current.LatencySpikeNotifyThreshold = 250;
        s.Current.RecentWindowSeconds = 120;
        s.Current.Targets.First(t => !t.IsGateway && !t.IsDnsResolver).Host = "9.9.9.10";
        s.Save();

        var reloaded = Create().Load();

        Assert.False(reloaded.IsBeginnerMode);
        Assert.Equal(250, reloaded.LatencySpikeNotifyThreshold);
        Assert.Equal(120, reloaded.RecentWindowSeconds);
        Assert.Contains(reloaded.Targets, t => t.Host == "9.9.9.10");
        Assert.Contains(reloaded.Targets, t => t.Protocol == NetworkProtocol.Dns);
    }

    [Fact]
    public void GatewayAddress_IsNotPersisted()
    {
        var s = Create();
        s.Load();
        var gw = s.Current.Targets.First(t => t.IsGateway);
        gw.Host = "192.168.7.1";
        gw.Name = "Gateway (192.168.7.1)";
        s.Save();

        var reloaded = Create().Load();
        var reloadedGw = reloaded.Targets.First(t => t.IsGateway);

        Assert.Equal("auto", reloadedGw.Host);
        Assert.DoesNotContain("192.168", reloadedGw.Name);
        // saving must not have modified the live settings
        Assert.Equal("192.168.7.1", gw.Host);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsAndIsPreserved()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var s = new SettingsService(path);
        var settings = s.Load();

        Assert.NotNull(s.LastLoadWarning);
        Assert.NotEmpty(settings.Targets);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"MonitoringIntervalMs\":1,\"RecentWindowSeconds\":999999,\"LatencySpikeNotifyThreshold\":-5,\"Targets\":[]}");

        var settings = new SettingsService(path).Load();

        Assert.Equal(250, settings.MonitoringIntervalMs);
        Assert.Equal(600, settings.RecentWindowSeconds);
        Assert.Equal(5, settings.LatencySpikeNotifyThreshold);
        Assert.NotEmpty(settings.Targets);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        var s = Create();
        s.Load();
        s.Save();

        Assert.False(File.Exists(s.FilePath + ".tmp"));
        Assert.True(File.Exists(s.FilePath));
    }

    [Fact]
    public void TrySave_ReportsFailureInsteadOfThrowing()
    {
        // a file where the directory should be makes directory creation fail
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var s = new SettingsService(Path.Combine(blocker, "settings.json"));
        s.Load();

        Assert.False(s.TrySave(out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
