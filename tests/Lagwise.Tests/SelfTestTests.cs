using Lagwise.Core.Games;

namespace Lagwise.Tests;

public class SelfTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lagwise-selftest-unit-" + Guid.NewGuid().ToString("N"));

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
        var old = Environment.GetEnvironmentVariable(Lagwise.Core.Services.SettingsService.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(Lagwise.Core.Services.SettingsService.DataDirectoryVariable, _dir);
            Assert.Equal(_dir, Lagwise.Core.Services.SettingsService.DefaultDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Lagwise.Core.Services.SettingsService.DataDirectoryVariable, old);
        }
        Assert.Contains("Lagwise", Lagwise.Core.Services.SettingsService.DefaultDirectory());
    }
}
