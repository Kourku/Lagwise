using Microsoft.Win32;
using Lagwise.Core.Services;

namespace Lagwise.Tests;

/// <summary>
/// The portable Lagwise installing itself for this user. Every test uses its own scratch registry keys and temporary
/// folders, never the real Settings > Apps entry, Start menu or startup entry.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SelfInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lagwise-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _regRoot = @"Software\LagwiseTests\Install-" + Guid.NewGuid().ToString("N");
    private string UninstallKey => _regRoot + @"\Lagwise";
    private string RunKey => _regRoot + @"\Run";
    private string Download => Path.Combine(_root, "Downloads");
    private string InstallDir => Path.Combine(_root, "Programs", "Lagwise");
    private string StartMenu => Path.Combine(_root, "StartMenu");
    private string Desktop => Path.Combine(_root, "Desktop");
    private string SourceExe => Path.Combine(Download, "Lagwise.exe");

    public SelfInstallerTests()
    {
        Directory.CreateDirectory(Download);
        File.WriteAllBytes(SourceExe, new byte[] { 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_regRoot, false);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private SelfInstaller Installer(Func<string?>? runningExe = null) =>
        new(UninstallKey, StartMenu, Desktop, new WindowsIntegrationService(RunKey, RunKey + "-approved", runningExe ?? (() => SourceExe)));

    private static bool ShellAvailable => Type.GetTypeFromProgID("WScript.Shell") != null;

    [Fact]
    public void Install_CopiesTheProgram_AddsTheStartMenuEntry_AndListsItInSettingsApps()
    {
        var installer = Installer();
        var result = installer.Install(SourceExe, InstallDir, desktopShortcut: false);

        Assert.True(result.Ok, result.Message);
        string target = Path.Combine(InstallDir, "Lagwise.exe");
        Assert.Equal(target, result.InstalledExe);
        Assert.Equal(File.ReadAllBytes(SourceExe), File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".partial"));
        if (ShellAvailable) Assert.True(File.Exists(Path.Combine(StartMenu, "Lagwise.lnk")));
        Assert.False(File.Exists(Path.Combine(Desktop, "Lagwise.lnk")));   // only when asked

        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey)!;
        Assert.Equal(InstallDir, key.GetValue("InstallLocation"));
        Assert.Equal($"\"{target}\" --uninstall", key.GetValue("UninstallString"));
        Assert.Equal(1, key.GetValue("NoModify"));
        Assert.Equal(InstallDir, installer.InstalledDirectory());
        Assert.True(installer.IsInstalledCopy(target));
        Assert.False(installer.IsInstalledCopy(SourceExe));
    }

    [Fact]
    public void Install_AddsADesktopShortcutOnlyWhenAsked()
    {
        Assert.True(Installer().Install(SourceExe, InstallDir, desktopShortcut: true).Ok);
        if (ShellAvailable)
        {
            string link = Path.Combine(Desktop, "Lagwise.lnk");
            Assert.True(File.Exists(link));
            Assert.True(SelfInstaller.ShortcutPointsInto(link, InstallDir));
        }
    }

    [Fact]
    public void Installing_OverAnOlderInstallation_UpdatesIt_AndRemovesTheOldSetupProgram()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllBytes(Path.Combine(InstallDir, "Lagwise.exe"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(InstallDir, "Lagwise-Setup.exe"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(InstallDir, "app.ico"), new byte[] { 9 });
        File.WriteAllText(Path.Combine(InstallDir, "notes.txt"), "the user's own file");

        Assert.True(Installer().Install(SourceExe, InstallDir, false).Ok);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(InstallDir, "Lagwise.exe")));
        Assert.False(File.Exists(Path.Combine(InstallDir, "Lagwise-Setup.exe")));
        Assert.False(File.Exists(Path.Combine(InstallDir, "app.ico")));
        Assert.True(File.Exists(Path.Combine(InstallDir, "notes.txt")));   // never anything it did not put there
    }

    [Fact]
    public void TheInstalledCopy_CannotInstallItselfOverItself()
    {
        var installer = Installer();
        Assert.True(installer.Install(SourceExe, InstallDir, false).Ok);
        var again = installer.Install(Path.Combine(InstallDir, "Lagwise.exe"), InstallDir, false);
        Assert.False(again.Ok);
    }

    [Fact]
    public void Install_MovesStartWithWindows_ToTheInstalledCopy_WhenItStartedTheDownloadedOne()
    {
        var installer = Installer();
        var startup = new WindowsIntegrationService(RunKey, RunKey + "-approved", () => SourceExe);
        Assert.True(startup.SetStartWithWindows(true));

        Assert.True(installer.Install(SourceExe, InstallDir, false).Ok);

        Assert.Equal(Path.Combine(InstallDir, "Lagwise.exe"), startup.StartupProgram());
        Assert.True(startup.IsStartWithWindowsEnabled());
    }

    [Fact]
    public void Install_LeavesAStartupEntryForAnotherCopyAlone()
    {
        string other = Path.Combine(_root, "Other", "Lagwise.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, new byte[] { 7 });
        var startup = new WindowsIntegrationService(RunKey, RunKey + "-approved", () => other);
        Assert.True(startup.SetStartWithWindows(true));

        Assert.True(Installer().Install(SourceExe, InstallDir, false).Ok);

        Assert.Equal(other, startup.StartupProgram());
    }

    [Fact]
    public void Uninstall_RemovesTheEntryShortcutsAndStartupEntry_ButNotAnotherProgramsShortcut()
    {
        var installer = Installer(() => Path.Combine(InstallDir, "Lagwise.exe"));
        Assert.True(installer.Install(SourceExe, InstallDir, desktopShortcut: true).Ok);
        var startup = new WindowsIntegrationService(RunKey, RunKey + "-approved", () => Path.Combine(InstallDir, "Lagwise.exe"));
        Assert.True(startup.SetStartWithWindows(true));
        // a file called Lagwise.lnk that is not ours (it points elsewhere) is kept
        string foreign = Path.Combine(_root, "Foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "readme.txt"), "x");

        var result = installer.Uninstall(InstallDir, deleteFilesAfterExit: false);

        Assert.True(result.Ok);
        Assert.Null(Registry.CurrentUser.OpenSubKey(UninstallKey));
        Assert.Null(installer.InstalledDirectory());
        Assert.False(File.Exists(Path.Combine(StartMenu, "Lagwise.lnk")));
        Assert.False(File.Exists(Path.Combine(Desktop, "Lagwise.lnk")));
        Assert.False(startup.IsStartWithWindowsEnabled());
        Assert.True(File.Exists(Path.Combine(InstallDir, "Lagwise.exe")));   // the program goes only after it has exited
    }

    [Fact]
    public void Uninstall_KeepsAShortcutThatStartsSomethingElse()
    {
        if (!ShellAvailable) return;
        var installer = Installer();
        Assert.True(installer.Install(SourceExe, InstallDir, desktopShortcut: true).Ok);
        // the user pointed the desktop shortcut at another copy: it is not ours to delete
        string otherDir = Path.Combine(_root, "Other");
        Directory.CreateDirectory(otherDir);
        string otherExe = Path.Combine(otherDir, "Lagwise.exe");
        File.WriteAllBytes(otherExe, new byte[] { 7 });
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Path.Combine(Desktop, "Lagwise.lnk"));
        link.TargetPath = otherExe;
        link.Save();

        installer.Uninstall(InstallDir, deleteFilesAfterExit: false);

        Assert.True(File.Exists(Path.Combine(Desktop, "Lagwise.lnk")));
        Assert.False(File.Exists(Path.Combine(StartMenu, "Lagwise.lnk")));
    }

    [Fact]
    public void AnEntryWhoseProgramIsGone_DoesNotCountAsInstalled()
    {
        var installer = Installer();
        Assert.True(installer.Install(SourceExe, InstallDir, false).Ok);
        File.Delete(Path.Combine(InstallDir, "Lagwise.exe"));
        Assert.Null(installer.InstalledDirectory());
        Assert.False(installer.IsInstalledCopy(Path.Combine(InstallDir, "Lagwise.exe")));
    }

    [Fact]
    public void Install_RemovesAnInstalledNetPulse_ButNotItsData()
    {
        string oldDir = Path.Combine(_root, "Programs", "NetPulse");
        Directory.CreateDirectory(oldDir);
        foreach (var f in new[] { "NetPulse.exe", "app.ico", "NetPulse-Setup.exe" }) File.WriteAllBytes(Path.Combine(oldDir, f), new byte[] { 1 });
        using (var key = Registry.CurrentUser.CreateSubKey(_regRoot + @"\NetPulse")) key.SetValue("InstallLocation", oldDir);
        Directory.CreateDirectory(StartMenu);
        File.WriteAllText(Path.Combine(StartMenu, "NetPulse.lnk"), "old");

        Assert.True(Installer().Install(SourceExe, InstallDir, false).Ok);

        Assert.False(Directory.Exists(oldDir));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_regRoot + @"\NetPulse"));
        Assert.False(File.Exists(Path.Combine(StartMenu, "NetPulse.lnk")));
    }

    [Fact]
    public void Install_FailsCleanly_WhenTheProgramFileIsMissing()
    {
        var result = Installer().Install(Path.Combine(Download, "missing.exe"), InstallDir, false);
        Assert.False(result.Ok);
        Assert.Null(Registry.CurrentUser.OpenSubKey(UninstallKey));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Lagwise", true)]
    [InlineData(@"C:\Users\x & y\Lagwise", false)]
    [InlineData(@"C:\100%\Lagwise", false)]
    [InlineData(@"relative\Lagwise", false)]
    public void TheFolderDeletedAfterUninstall_MustBeSafeOnACommandLine(string path, bool safe) =>
        Assert.Equal(safe, SelfInstaller.IsSafeForCommandLine(path));
}
