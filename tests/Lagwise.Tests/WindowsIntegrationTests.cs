using System.Net;
using Microsoft.Win32;
using Lagwise.Core;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

/// <summary>How Lagwise fits into Windows: one running copy, sleep and wake, the startup entry, Wi-Fi and the adapter in use.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsIntegrationTests
{
    // ---- one running copy per data folder

    [Fact]
    public void InstanceKey_IsPerDataFolder_AndIgnoresCaseAndTrailingSlash()
    {
        string a = AppInstance.KeyFor(@"C:\Users\x\AppData\Local\Lagwise");
        Assert.Equal(a, AppInstance.KeyFor(@"c:\users\X\AppData\Local\Lagwise\"));
        Assert.NotEqual(a, AppInstance.KeyFor(@"C:\Temp\lagwise-test"));
        Assert.StartsWith("Lagwise-", a);
        Assert.DoesNotContain('\\', a);   // usable as a pipe and mutex name
    }

    [Theory]
    [InlineData(new string[0], AppInstance.Show)]
    [InlineData(new[] { "--minimized" }, AppInstance.None)]
    [InlineData(new[] { "--command", "mark-lag" }, AppInstance.MarkLag)]
    [InlineData(new[] { "--command", "SPEEDTEST" }, AppInstance.SpeedTest)]
    [InlineData(new[] { "--command", "format-c" }, AppInstance.Show)]
    [InlineData(new[] { "--command" }, AppInstance.Show)]
    public void CommandLine_BecomesOneOfTheKnownRequests(string[] args, string expected) =>
        Assert.Equal(expected, AppInstance.CommandFromArgs(args));

    [Theory]
    [InlineData("exit\n", AppInstance.Exit)]
    [InlineData("  History \r\n", AppInstance.History)]
    [InlineData("history\nexit\n", AppInstance.History)]
    [InlineData("rm -rf\n", null)]
    [InlineData("", null)]
    public void OnlyKnownWords_AreAccepted(string sent, string? expected) => Assert.Equal(expected, AppInstance.Parse(sent));

    [Fact]
    public async Task ASecondStart_HandsItsRequestToTheRunningCopy()
    {
        string key = "Lagwise-Test-" + Guid.NewGuid().ToString("N");
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var listening = AppInstance.ListenAsync(key, c => got.TrySetResult(c), cts.Token);

        Assert.True(await Task.Run(() => AppInstance.TrySend(key, AppInstance.MarkLag, TimeSpan.FromSeconds(5))));
        Assert.Equal(AppInstance.MarkLag, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // the copy keeps listening for the next start
        got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await Task.Run(() => AppInstance.TrySend(key, AppInstance.Exit, TimeSpan.FromSeconds(5))));
        Assert.Equal(AppInstance.Exit, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheRequestChannel_RefusesClientsComingThroughTheNetwork()
    {
        if (!OperatingSystem.IsWindows()) return;
        string key = "Lagwise-Test-" + Guid.NewGuid().ToString("N");
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var listening = AppInstance.ListenAsync(key, c => got.TrySetResult(c), cts.Token);
        Assert.True(await Task.Run(() => AppInstance.TrySend(key, AppInstance.Show, TimeSpan.FromSeconds(5))));   // listening, locally

        // \\<this PC>\pipe\... goes through file sharing, as another computer would: Windows must refuse it
        await Task.Run(() =>
        {
            using var remote = new System.IO.Pipes.NamedPipeClientStream(Environment.MachineName, key, System.IO.Pipes.PipeDirection.Out);
            Assert.ThrowsAny<Exception>(() => remote.Connect(3000));
        });

        cts.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WithNoRunningCopy_TheRequestIsNotDelivered()
    {
        Assert.False(AppInstance.TrySend("Lagwise-Test-" + Guid.NewGuid().ToString("N"), AppInstance.Show, TimeSpan.FromMilliseconds(200)));
    }

    // ---- sleep and wake

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (MeasurementEngine Engine, EventLogService Events, List<DateTime> Cycles) Engine()
    {
        var settings = new AppSettings
        {
            Targets = new List<TargetConfig>
            {
                new() { Id = "gw", Name = "Gateway", Host = "192.168.0.1", IsGateway = true },
                new() { Id = "ref", Name = "Reference", Host = "203.0.113.1" }
            },
            RecentWindowSeconds = 10
        };
        var events = new EventLogService();
        var engine = new MeasurementEngine(new NetworkDiscoveryService(), new PingService(), new TcpProbeService(),
            new HttpProbeService(), new DnsProbeService(), new DiagnosticEngine(), events) { Settings = settings };
        var cycles = new List<DateTime>();
        engine.CycleCompleted += (_, t) => cycles.Add(t);
        return (engine, events, cycles);
    }

    private static Measurement M(string id, DateTime t, bool ok) => new()
    {
        TargetId = id, TargetName = id, TargetHost = "h", Timestamp = t, Success = ok, RttMs = ok ? 20 : 0, Protocol = NetworkProtocol.Icmp
    };

    private static void Cycle(MeasurementEngine e, DateTime t, bool router, bool internet) =>
        e.IngestCycle(new[] { M("gw", t, router), M("ref", t, internet) }, t);

    [Fact]
    public void AfterWaking_TheReconnectingAdapter_IsAGapNotADrop()
    {
        var (engine, events, cycles) = Engine();
        for (int s = 0; s < 5; s++) Cycle(engine, T0.AddSeconds(s), true, true);

        var woke = T0.AddMinutes(30);
        engine.NoteResumed(woke);
        for (int s = 0; s < 10; s++) Cycle(engine, woke.AddSeconds(s), false, false);   // Wi-Fi reconnecting
        Cycle(engine, woke.AddSeconds(10), true, true);

        Assert.DoesNotContain(events.GetRecentEvents(100), e => e.EventType == "Disconnected");
        Assert.Equal(6, cycles.Count);                         // the silent cycles were not measured time
        Assert.Equal(woke.AddSeconds(10), cycles[^1]);
        Assert.False(engine.IsWaking(woke.AddSeconds(11)));    // back to normal once the internet answered
    }

    [Fact]
    public void ANetworkThatStaysDown_AfterTheWakeGrace_IsStillReportedAsADrop()
    {
        var (engine, events, _) = Engine();
        Cycle(engine, T0, true, true);
        var woke = T0.AddHours(1);
        engine.NoteResumed(woke);
        int last = (int)MeasurementEngine.WakeGrace.TotalSeconds + 5;
        for (int s = 0; s <= last; s++) Cycle(engine, woke.AddSeconds(s), false, false);

        var down = Assert.Single(events.GetRecentEvents(100), e => e.EventType == "Disconnected");
        Assert.True(down.Timestamp >= woke + MeasurementEngine.WakeGrace);   // counted from when the grace ended, not from waking
    }

    [Fact]
    public void GoingToSleep_DoesNotCountAsADrop_AndRealDropsStillDo()
    {
        var (engine, events, _) = Engine();
        Cycle(engine, T0, true, true);
        engine.NoteSuspending(T0.AddSeconds(1));
        for (int s = 1; s < 6; s++) Cycle(engine, T0.AddSeconds(s), false, false);
        Assert.DoesNotContain(events.GetRecentEvents(100), e => e.EventType == "Disconnected");

        // a normal drop, well after any sleep or wake
        var later = T0.AddHours(2);
        Cycle(engine, later, true, true);
        for (int s = 1; s < 6; s++) Cycle(engine, later.AddSeconds(s), true, false);
        Assert.Single(events.GetRecentEvents(100), e => e.EventType == "Disconnected");
    }

    // ---- start with Windows

    private sealed class ScratchKeys : IDisposable
    {
        public string Root { get; } = @"Software\LagwiseTests\" + Guid.NewGuid().ToString("N");
        public string Run => Root + @"\Run";
        public string Approved => Root + @"\Approved";
        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Root, false);
    }

    [Fact]
    public void StartWithWindows_RespectsTheSwitchInTaskManager_AndSwitchingOnInLagwiseLiftsIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var keys = new ScratchKeys();
        string exe = Environment.ProcessPath!;
        var w = new WindowsIntegrationService(keys.Run, keys.Approved, () => exe);

        Assert.False(w.IsStartWithWindowsEnabled());
        Assert.True(w.SetStartWithWindows(true));
        Assert.True(w.IsStartWithWindowsEnabled());
        Assert.Equal(WindowsIntegrationService.CommandFor(exe), Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("Lagwise"));

        // turned off in Task Manager > Startup apps: Windows will not start it, so Lagwise must not say it will
        using (var a = Registry.CurrentUser.CreateSubKey(keys.Approved)) a.SetValue("Lagwise", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(w.IsTurnedOffInWindows());
        Assert.False(w.IsStartWithWindowsEnabled());

        Assert.True(w.SetStartWithWindows(true));
        Assert.False(w.IsTurnedOffInWindows());
        Assert.True(w.IsStartWithWindowsEnabled());

        Assert.True(w.SetStartWithWindows(false));
        Assert.False(w.IsStartWithWindowsEnabled());
    }

    [Fact]
    public void AMovedPortableCopy_RepairsItsStartupEntry_ButNeverTakesOverAnotherCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var keys = new ScratchKeys();
        string exe = Environment.ProcessPath!;
        var w = new WindowsIntegrationService(keys.Run, keys.Approved, () => exe);
        using (var run = Registry.CurrentUser.CreateSubKey(keys.Run)) run.SetValue("Lagwise", "\"C:\\gone\\Lagwise.exe\" --minimized");

        Assert.True(w.RepairStartupEntry());
        Assert.Equal(WindowsIntegrationService.CommandFor(exe), Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("Lagwise"));

        // another copy that still exists keeps its entry
        string other = Path.Combine(Path.GetTempPath(), "lagwise-other-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(other, "");
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(keys.Run)) run.SetValue("Lagwise", WindowsIntegrationService.CommandFor(other));
            Assert.False(w.RepairStartupEntry());
            Assert.Equal(WindowsIntegrationService.CommandFor(other), Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("Lagwise"));
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void Uninstalling_RemovesTheStartupEntryOfThatCopyOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var keys = new ScratchKeys();
        var w = new WindowsIntegrationService(keys.Run, keys.Approved, () => Environment.ProcessPath);
        using (var run = Registry.CurrentUser.CreateSubKey(keys.Run)) run.SetValue("Lagwise", @"""C:\Users\x\AppData\Local\Programs\Lagwise\Lagwise.exe"" --minimized");
        using (var a = Registry.CurrentUser.CreateSubKey(keys.Approved)) a.SetValue("Lagwise", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);

        Assert.False(w.RemoveStartupEntryFor(@"D:\Tools\Lagwise"));          // another copy's folder: left alone
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("Lagwise"));

        Assert.True(w.RemoveStartupEntryFor(@"C:\Users\x\AppData\Local\Programs\Lagwise\"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("Lagwise"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(keys.Approved)!.GetValue("Lagwise"));   // no stale Task Manager entry either
    }

    // ---- from NetPulse (the old name) to Lagwise

    [Fact]
    public void TheFirstStartAfterTheRename_CopiesNetPulsesData_AndLeavesTheOldFolderAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "lagwise-rename-" + Guid.NewGuid().ToString("N"));
        string oldDir = Path.Combine(root, "NetPulse"), newDir = Path.Combine(root, "Lagwise");
        try
        {
            Directory.CreateDirectory(Path.Combine(oldDir, "sessions"));
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"Theme\":\"Light\"}");
            File.WriteAllText(Path.Combine(oldDir, "sessions", "a.session.json"), "{}");
            File.WriteAllText(Path.Combine(oldDir, "settings.json.tmp"), "half");

            Assert.True(LegacyNetPulse.CopyDataIfFirstStart(oldDir, newDir));
            Assert.Equal("{\"Theme\":\"Light\"}", File.ReadAllText(Path.Combine(newDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "sessions", "a.session.json")));
            Assert.False(File.Exists(Path.Combine(newDir, "settings.json.tmp")));    // an interrupted write is not data
            Assert.True(File.Exists(Path.Combine(oldDir, "settings.json")));         // the old folder stays as it was

            // once Lagwise has its own folder, nothing is copied again
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"Theme\":\"Dark\"}");
            Assert.False(LegacyNetPulse.CopyDataIfFirstStart(oldDir, newDir));
            Assert.Equal("{\"Theme\":\"Light\"}", File.ReadAllText(Path.Combine(newDir, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WithoutNetPulseData_NothingIsCreated()
    {
        string root = Path.Combine(Path.GetTempPath(), "lagwise-rename-" + Guid.NewGuid().ToString("N"));
        Assert.False(LegacyNetPulse.CopyDataIfFirstStart(Path.Combine(root, "NetPulse"), Path.Combine(root, "Lagwise")));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false, true)]   // switched on: Lagwise takes its place
    [InlineData(true, false)]   // turned off in Task Manager: stays off
    public void NetPulsesStartupEntry_BecomesLagwises_OnlyIfItWasOn(bool turnedOff, bool expectOn)
    {
        using var keys = new ScratchKeys();
        string exe = Environment.ProcessPath!;
        var w = new WindowsIntegrationService(keys.Run, keys.Approved, () => exe);
        using (var run = Registry.CurrentUser.CreateSubKey(keys.Run)) run.SetValue("NetPulse", @"""C:\Old\NetPulse.exe"" --minimized");
        if (turnedOff)
            using (var a = Registry.CurrentUser.CreateSubKey(keys.Approved)) a.SetValue("NetPulse", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);

        Assert.True(w.MigrateStartupEntry("NetPulse"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(keys.Run)!.GetValue("NetPulse"));
        Assert.Equal(expectOn, w.IsStartWithWindowsEnabled());
        Assert.False(w.MigrateStartupEntry("NetPulse"));   // done once
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Lagwise\\Lagwise.exe\" --minimized", "C:\\Program Files\\Lagwise\\Lagwise.exe")]
    [InlineData("C:\\Tools\\Lagwise.exe --minimized", "C:\\Tools\\Lagwise.exe")]
    [InlineData("C:\\Tools\\Lagwise.exe", "C:\\Tools\\Lagwise.exe")]
    public void TheProgram_IsFoundInARunCommand(string command, string program) =>
        Assert.Equal(program, WindowsIntegrationService.ProgramIn(command));

    // ---- Wi-Fi from Windows' WLAN API

    private static byte[] Connection(int state = 1, string ssid = "Home", int signal = 78, int phy = 10, uint rx = 1_200_900, uint tx = 864_000, int auth = 7, int cipher = 4)
    {
        var a = new byte[WlanInfo.ConnectionAttributesSize];
        BitConverter.GetBytes(state).CopyTo(a, 0);
        var name = System.Text.Encoding.UTF8.GetBytes(ssid);
        BitConverter.GetBytes(name.Length).CopyTo(a, 520);
        name.CopyTo(a, 524);
        new byte[] { 0x1c, 0x2b, 0x3a, 0x49, 0x58, 0x67 }.CopyTo(a, 560);
        BitConverter.GetBytes(phy).CopyTo(a, 568);
        BitConverter.GetBytes(signal).CopyTo(a, 576);
        BitConverter.GetBytes(rx).CopyTo(a, 580);
        BitConverter.GetBytes(tx).CopyTo(a, 584);
        BitConverter.GetBytes(auth).CopyTo(a, 596);
        BitConverter.GetBytes(cipher).CopyTo(a, 600);
        return a;
    }

    [Fact]
    public void WlanConnection_IsReadIntoTheSameWordsAsNetsh()
    {
        var info = new NetworkInterfaceInfo();
        Assert.True(WlanInfo.ReadConnection(Connection(), info));
        Assert.Equal("Home", info.WifiSsid);
        Assert.Equal(78, info.WifiSignalPercent);
        Assert.Equal("1c:2b:3a:49:58:67", info.WifiBssid);
        Assert.Equal("802.11ax", info.WifiRadioType);
        Assert.Equal("1200.9", info.WifiReceiveRate);
        Assert.Equal("864", info.WifiTransmitRate);
        Assert.Equal("WPA2-Personal", info.WifiAuthentication);
        Assert.Equal("CCMP", info.WifiCipher);
        // the security check reads these words as it did netsh's
        Assert.Equal(Lagwise.Core.Security.SecurityCheck.WifiProtection.Wpa2, Lagwise.Core.Security.SecurityCheck.WifiKind(info.WifiAuthentication, info.WifiCipher));
    }

    [Fact]
    public void WlanProtection_MapsToTheSecurityCheck()
    {
        Lagwise.Core.Security.SecurityCheck.WifiProtection Kind(int auth, int cipher)
        {
            var i = new NetworkInterfaceInfo();
            WlanInfo.ReadConnection(Connection(auth: auth, cipher: cipher), i);
            return Lagwise.Core.Security.SecurityCheck.WifiKind(i.WifiAuthentication, i.WifiCipher);
        }
        Assert.Equal(Lagwise.Core.Security.SecurityCheck.WifiProtection.Wpa3, Kind(9, 4));     // SAE
        Assert.Equal(Lagwise.Core.Security.SecurityCheck.WifiProtection.Open, Kind(1, 0));     // open, no cipher
        Assert.Equal(Lagwise.Core.Security.SecurityCheck.WifiProtection.Weak, Kind(1, 5));     // WEP
        Assert.Equal(Lagwise.Core.Security.SecurityCheck.WifiProtection.Weak, Kind(7, 2));     // WPA2 with TKIP
    }

    [Fact]
    public void ADisconnectedOrImplausibleWlanReading_ChangesNothing()
    {
        var info = new NetworkInterfaceInfo { WifiSignalPercent = 55 };
        Assert.False(WlanInfo.ReadConnection(Connection(state: 4), info));          // disconnected
        Assert.False(WlanInfo.ReadConnection(Connection(signal: 140), info));       // not a percentage
        Assert.False(WlanInfo.ReadConnection(new byte[100], info));                 // too short
        Assert.Equal(55, info.WifiSignalPercent);
    }

    [Fact]
    public void Netsh_PointingAtLocationSettings_MeansWindowsWithheldTheWifiDetails()
    {
        var info = new NetworkInterfaceInfo();
        NetworkDiscoveryService.ParseWlanInterfaces(
            "Network shell commands need location permission to access WLAN information. Turn on Location services on the Location page in Privacy & security settings.\r\n\r\n" +
            "Here is the URI for the Location page in the Settings app:\r\nms-settings:privacy-location\r\n", info);
        Assert.True(info.WifiNeedsLocationPermission);
        Assert.Null(info.WifiSignalPercent);
    }

    // ---- the adapter in use, and VPNs

    private sealed record Nic(string Description);

    [Fact]
    public void TheRouterMeasured_IsOnTheAdapterWindowsRoutesThrough()
    {
        var ethernet = new Nic("Realtek Gaming 2.5GbE Family Controller");
        var wifi = new Nic("Intel(R) Wi-Fi 6E AX211 160MHz");
        var candidates = new List<(Nic, IPAddress)> { (ethernet, IPAddress.Parse("192.168.1.1")), (wifi, IPAddress.Parse("192.168.50.1")) };

        Assert.Same(wifi, NetworkDiscoveryService.PickActive(wifi, candidates, n => n.Description).Nic);
        Assert.Equal(IPAddress.Parse("192.168.50.1"), NetworkDiscoveryService.PickActive(wifi, candidates, n => n.Description).Gateway);
    }

    [Fact]
    public void WithAVpnWithoutARouter_ARealAdapterIsMeasured_BeforeAVirtualSwitch()
    {
        var hyperV = new Nic("Hyper-V Virtual Ethernet Adapter");
        var ethernet = new Nic("Intel(R) Ethernet Connection I219-V");
        var vpn = new Nic("WireGuard Tunnel");
        var candidates = new List<(Nic, IPAddress)> { (hyperV, IPAddress.Parse("172.20.0.1")), (ethernet, IPAddress.Parse("192.168.1.1")) };

        Assert.Same(ethernet, NetworkDiscoveryService.PickActive(vpn, candidates, n => n.Description).Nic);
        Assert.Null(NetworkDiscoveryService.PickActive(vpn, new List<(Nic, IPAddress)>(), n => n.Description).Nic);
    }

    [Theory]
    [InlineData("{1}", "Ethernet", "Realtek PCIe GbE Family Controller", "{1}", null)]
    [InlineData("{2}", "NordLynx", "NordLynx Tunnel", "{1}", "NordLynx (NordLynx Tunnel)")]
    [InlineData("{2}", "Local Area Connection", "TAP-Windows Adapter V9", "{2}", "Local Area Connection (TAP-Windows Adapter V9)")]
    [InlineData("{2}", "Office", "Office", "{1}", "Office")]
    [InlineData("{1}", "vEthernet (External)", "Hyper-V Virtual Ethernet Adapter", "{1}", null)]
    public void AVpnOrTunnel_IsNamed_WhenInternetTrafficGoesThroughIt(string egressId, string name, string description, string activeId, string? expected) =>
        Assert.Equal(expected, NetworkDiscoveryService.TunnelFor(egressId, name, description, activeId));

    [Theory]
    [InlineData("Wintun Userspace Tunnel", true)]
    [InlineData("Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", true)]
    [InlineData("Bitdefender TAP Adapter", true)]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("Realtek Gaming 2.5GbE Family Controller", false)]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", false)]
    public void VpnAdapters_AreRecognisedByName(string description, bool vpn) =>
        Assert.Equal(vpn, NetworkDiscoveryService.LooksLikeVpn(description));

    // ---- recordings remember the VPN

    [Fact]
    public void TheVpnIsKeptWithTheRecording_AndNamedWhenLagWasBeyondTheRouter()
    {
        var rec = new SessionRecorder(() => T0);
        var session = rec.Start(null, "Test", new List<TargetConfig>(), new NetworkInterfaceInfo { Name = "Ethernet", InterfaceType = "Ethernet", TunnelName = "NordLynx" }, 1000);
        Assert.Equal("NordLynx", session.Vpn);
        Assert.Equal("NordLynx", rec.Snapshot()!.Vpn);
    }

    // ---- History list

    [Theory]
    [InlineData("Your connection was steady", "Steady")]
    [InlineData("Lag came from your Wi-Fi or router", "Wi-Fi or router")]
    [InlineData("Lag came from beyond your router", "Beyond your router")]
    [InlineData("Lag came from this PC's own downloads", "This PC's downloads")]
    [InlineData("Something new in a later version", "Something new in a later version")]
    [InlineData(null, "")]
    public void TheHistoryList_ShowsTheVerdictInAFewWords(string? headline, string label) =>
        Assert.Equal(label, SessionVerdict.ShortLabel(headline));

    // ---- Microsoft Store edition

    private sealed class FakeStartupTask : IStartupTaskBackend
    {
        public StartupTaskState State = StartupTaskState.Disabled;
        public StartupTaskState GetState() => State;
        public StartupTaskState Enable() { if (State == StartupTaskState.Disabled) State = StartupTaskState.Enabled; return State; }
        public void Disable() { if (State == StartupTaskState.Enabled) State = StartupTaskState.Disabled; }
    }

    private static (WindowsIntegrationService Service, FakeStartupTask Task, string Run) PackagedService()
    {
        string run = @"Software\LagwiseTests\Run-" + Guid.NewGuid().ToString("N");
        var task = new FakeStartupTask();
        var service = new WindowsIntegrationService(run, run + "-approved", () => null);
        service.UseStartupTask(task);
        return (service, task, run);
    }

    [Fact]
    public void Packaged_StartWithWindows_UsesTheStartupTask_NotTheRunKey()
    {
        var (service, task, run) = PackagedService();
        try
        {
            Assert.False(service.IsStartWithWindowsEnabled());
            Assert.True(service.SetStartWithWindows(true));
            Assert.Equal(StartupTaskState.Enabled, task.State);
            Assert.True(service.IsStartWithWindowsEnabled());
            Assert.Null(Registry.CurrentUser.OpenSubKey(run));   // nothing written to the registry
            Assert.True(service.SetStartWithWindows(false));
            Assert.False(service.IsStartWithWindowsEnabled());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(run, false); }
    }

    [Theory]
    [InlineData(StartupTaskState.DisabledByUser)]
    [InlineData(StartupTaskState.DisabledByPolicy)]
    public void Packaged_ASwitchOffByTheUserOrPolicy_IsReportedAndCannotBeOverridden(StartupTaskState state)
    {
        var (service, task, run) = PackagedService();
        try
        {
            task.State = state;
            Assert.True(service.IsTurnedOffInWindows());
            Assert.False(service.IsStartWithWindowsEnabled());
            Assert.False(service.SetStartWithWindows(true));     // only the user can switch it back on
            Assert.Equal(state, task.State);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(run, false); }
    }

    [Fact]
    public void Packaged_NeverTouchesRunEntriesOfOtherEditions()
    {
        var (service, _, run) = PackagedService();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(run)) key.SetValue("NetPulse", @"""C:\old\NetPulse.exe"" --minimized");
            Assert.False(service.MigrateStartupEntry("NetPulse"));
            Assert.False(service.RepairStartupEntry());
            using var after = Registry.CurrentUser.OpenSubKey(run);
            Assert.NotNull(after!.GetValue("NetPulse"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(run, false); }
    }

    [Fact]
    public void LaunchPath_IsTheRunningProgram_UnlessPackaged_ThenTheAlias()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lagwise-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(@"C:\App\Lagwise.exe", PackageContext.LaunchPath(@"C:\App\Lagwise.exe", packaged: false, aliasFolder: folder));
            // packaged, but the alias is not there yet: fall back to the running program rather than a path that does not exist
            Assert.Equal(@"C:\Pkg\Lagwise.exe", PackageContext.LaunchPath(@"C:\Pkg\Lagwise.exe", packaged: true, aliasFolder: folder));
            File.WriteAllText(Path.Combine(folder, "Lagwise.exe"), "");
            Assert.Equal(Path.Combine(folder, "Lagwise.exe"), PackageContext.LaunchPath(@"C:\Pkg\Lagwise.exe", packaged: true, aliasFolder: folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ATestRun_IsNotPackaged() => Assert.False(PackageContext.IsPackaged);
}
