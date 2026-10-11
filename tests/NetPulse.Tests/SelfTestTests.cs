using NetPulse.Core.Games;

namespace NetPulse.Tests;

public class SelfTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpulse-selftest-unit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void SelfTest_PassesInTheDevelopmentBuild()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = SelfTest.Run(_dir);

        Assert.True(report.Ok, string.Join("\n", report.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.True(report.Checks.Count >= 6);
        Assert.Contains("\"Ok\": true", SelfTest.ToJson(report));
    }

    [Fact]
    public void DataDirectory_CanBeRedirectedByEnvironmentVariable_ForIsolatedRuns()
    {
        var old = Environment.GetEnvironmentVariable(NetPulse.Core.Services.SettingsService.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(NetPulse.Core.Services.SettingsService.DataDirectoryVariable, _dir);
            Assert.Equal(_dir, NetPulse.Core.Services.SettingsService.DefaultDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(NetPulse.Core.Services.SettingsService.DataDirectoryVariable, old);
        }
        Assert.Contains("NetPulse", NetPulse.Core.Services.SettingsService.DefaultDirectory());
    }
}
