using System.Net;
using Lagwise.Core.Games;
using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class ActivityInsightTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 21, 0, 0, DateTimeKind.Utc);

    private static RawConnection Tcp(int pid, string remote, ConnectionState state = ConnectionState.Established) =>
        new(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.20"), 50000 + pid, IPAddress.Parse(remote), 443, state, pid);

    private static RawConnection Udp(int pid) =>
        new(TransportProtocol.Udp, IPAddress.Any, 60000 + pid, null, 0, ConnectionState.NotApplicable, pid);

    private static readonly Dictionary<int, string> Names = new()
    {
        [10] = "steam", [11] = "steamwebhelper", [20] = "OneDrive", [30] = "League of Legends", [40] = "notepad", [50] = "Lagwise", [60] = "chrome", [70] = "LeagueClient"
    };

    private static IReadOnlyList<OnlineProgram> Summarize(params RawConnection[] c) =>
        OnlinePrograms.Summarize(c, pid => Names.GetValueOrDefault(pid), OnlinePrograms.FromProfiles(GameProfiles.All), ownPid: 50);

    [Fact]
    public void Programs_AreGroupedByName_AndHelperProcessesMerge()
    {
        var list = Summarize(Tcp(10, "23.1.1.1"), Tcp(10, "23.1.1.2"), Tcp(11, "23.1.1.2"), Tcp(20, "13.1.1.1"));
        var steam = list.Single(p => p.DisplayName == "Steam");
        Assert.Equal(2, steam.ProcessCount);
        Assert.Equal(3, steam.InternetConnections);
        Assert.Equal(2, steam.RemoteHosts);
        Assert.Equal(ProgramKind.GameLauncher, steam.Kind);
        Assert.True(steam.CanUseALot);
        Assert.Equal(ProgramKind.CloudSync, list.Single(p => p.DisplayName == "OneDrive").Kind);
    }

    [Fact]
    public void LocalTraffic_ClosedSockets_UdpOnly_AndLagwiseItself_AreLeftOut()
    {
        var list = Summarize(
            Tcp(40, "192.168.1.1"),                                  // home network
            Tcp(40, "127.0.0.1"),                                    // this PC
            Tcp(40, "23.9.9.9", ConnectionState.TimeWait),           // closing
            Udp(60),                                                 // UDP alone
            Tcp(50, "1.1.1.1"));                                     // Lagwise
        Assert.Empty(list);
    }

    [Fact]
    public void HeavyCapablePrograms_ComeFirst_AndGamesAreRecognised()
    {
        var list = Summarize(Tcp(40, "23.9.9.9"), Tcp(40, "23.9.9.8"), Tcp(40, "23.9.9.7"), Tcp(30, "104.1.1.1"), Tcp(60, "142.1.1.1"));
        Assert.Equal("Google Chrome", list[0].DisplayName);
        Assert.Equal(ProgramKind.Game, list.Single(p => p.DisplayName == "League of Legends").Kind);
        Assert.False(list.Single(p => p.DisplayName == "notepad").CanUseALot);
    }

    [Fact]
    public void GameClients_FromTheProfiles_AreLaunchers()
    {
        var client = Summarize(Tcp(70, "104.2.2.2")).Single();
        Assert.Equal("League of Legends (client)", client.DisplayName);
        Assert.Equal(ProgramKind.GameLauncher, client.Kind);
    }

    [Fact]
    public void HeavyCandidates_NameOnlyProgramsThatOftenMoveALot()
    {
        var list = Summarize(Tcp(10, "23.1.1.1"), Tcp(10, "23.1.1.2"), Tcp(40, "23.9.9.9"), Tcp(20, "13.1.1.1"));
        Assert.Equal("Steam (2 connections), OneDrive (1)", OnlinePrograms.HeavyCandidatesText(list));
        Assert.Equal("", OnlinePrograms.HeavyCandidatesText(Summarize(Tcp(40, "23.9.9.9"))));
    }

    [Fact]
    public void ReferencePing_IsOneMedianPerSecond_FromPlainPingReferencesOnly()
    {
        Measurement M(string id, double ms, double sec, NetworkProtocol p = NetworkProtocol.Icmp, bool ok = true) =>
            new() { TargetId = id, RttMs = ms, Timestamp = T0.AddSeconds(sec), Protocol = p, Success = ok };
        var history = new[]
        {
            M("cf", 10, 0.1), M("g", 30, 0.2), M("q", 20, 0.3),             // second 0: median 20
            M("gw", 1, 0.4), M("web", 300, 0.5, NetworkProtocol.Https),     // not references
            M("cf", 12, 1.1), M("g", 999, 1.2, ok: false), M("q", 14, 1.3)  // second 1: failed probe ignored, median 13
        };
        var series = ReferencePing.PerSecond(history, new HashSet<string> { "cf", "g", "q", "web" }, T0);
        Assert.Equal(2, series.Count);
        Assert.Equal(20, series[0].Ms);
        Assert.Equal(13, series[1].Ms);
        Assert.Equal(T0.AddSeconds(0.5), series[0].T);
    }

    [Fact]
    public void Recordings_KeepEachSecondOfTraffic()
    {
        var rec = new SessionRecorder(() => T0);
        rec.RecordThroughput(T0, 1_000_000, 50_000);                 // not recording yet: ignored
        rec.Start(null, "test", Array.Empty<TargetConfig>(), null, 1000);
        rec.RecordThroughput(T0.AddSeconds(1), 12_345_678.9, 98_765.4);
        rec.RecordThroughput(T0.AddSeconds(2), 0, 0);
        var s = rec.Stop()!;
        Assert.Equal(2, s.Throughput.Count);
        Assert.Equal(12_345_679, s.Throughput[0].Rx);
        Assert.Equal(98_765, s.Throughput[0].Tx);
    }

    [Fact]
    public void HeavyTrafficLimit_ZeroTurnsItOff()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-activity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new LagwiseHost(dir);
            host.Settings.HeavyTrafficMbps = 0;
            host.ApplyActivitySettings();
            Assert.True(double.IsPositiveInfinity(host.Activity.HeavyTrafficBps));
            host.Settings.HeavyTrafficMbps = 30;
            host.ApplyActivitySettings();
            Assert.Equal(30_000_000, host.Activity.HeavyTrafficBps);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
