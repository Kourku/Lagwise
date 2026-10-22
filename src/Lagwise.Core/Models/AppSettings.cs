using System.Collections.Generic;
using System.Linq;

namespace Lagwise.Core.Models;

public class AppSettings
{
    /// <summary>Version of the settings layout; older files are upgraded once on load (see <see cref="Upgrade"/>).</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;
    public const int CurrentSchema = 2;

    /// <summary>
    /// Brings settings written by an older Lagwise up to date. 2 (Lagwise 1.0): the Game Monitor watches all games
    /// rather than the one game that had to be picked before, and records by itself (its new default), once.
    /// </summary>
    public void Upgrade()
    {
        if (SchemaVersion < 2)
        {
            LastGameId = Games.GameMonitorService.AllGames;
            AutoRecordGame = true;
        }
        SchemaVersion = CurrentSchema;
    }

    public int MonitoringIntervalMs { get; set; } = 1000;
    public int HistoryWindowMinutes { get; set; } = 15;
    /// <summary>Length of the "recent conditions" window used for loss and latency variation.</summary>
    public int RecentWindowSeconds { get; set; } = 60;
    /// <summary>Identical recurring events (same type and target) within this period are collapsed.</summary>
    public int EventDedupSeconds { get; set; } = 30;
    /// <summary>Id of the Speed Test / Bufferbloat provider (see TransferProviders).</summary>
    public string TransferProviderId { get; set; } = "cloudflare";
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = false;
    public bool DarkTheme { get; set; } = true;

    // Notifications
    public bool NotifyOnDisconnect { get; set; } = true;
    public bool NotifyOnPacketLoss { get; set; } = true;
    public double PacketLossNotifyThreshold { get; set; } = 5.0; // percent
    public bool NotifyOnLatencySpike { get; set; } = true;
    public double LatencySpikeNotifyThreshold { get; set; } = 100.0; // ms
    public bool NotifyOnRouteChange { get; set; } = true;

    // UI state
    public bool IsBeginnerMode { get; set; } = true;
    /// <summary>False until the user has answered the "how much detail?" welcome card once.</summary>
    public bool ModeChosen { get; set; } = false;
    /// <summary>What the Game Monitor watches: "*" (all games, also when unset) or one game's id.</summary>
    public string? LastGameId { get; set; }

    // Game Monitor behaviour
    /// <summary>Start recording when a watched game starts, and stop and save once it has been closed for a short while.</summary>
    public bool AutoRecordGame { get; set; } = true;
    /// <summary>Programs added as games (full paths to .exe files).</summary>
    public List<string> UserGames { get; set; } = new();
    /// <summary>Games taken off the list (ids), so Lagwise does not watch for them.</summary>
    public List<string> IgnoredGames { get; set; } = new();
    /// <summary>Automatic recordings kept (the newest; ones with marked lag are always kept); 0 keeps all.</summary>
    public int AutoRecordingsKept { get; set; } = 50;
    /// <summary>System-wide shortcut that marks lag while a recording runs (see <see cref="HotkeyGesture"/>); "Off" disables it.</summary>
    public string MarkLagHotkey { get; set; } = "Ctrl+Shift+F9";
    /// <summary>Play a short sound when lag is marked with the shortcut, so the player knows it worked without leaving the game.</summary>
    public bool MarkLagSound { get; set; } = true;
    /// <summary>Traffic on this PC above this many Mbps for 10 s is noted in the timeline; 0 turns it off.</summary>
    public int HeavyTrafficMbps { get; set; } = 15;
    /// <summary>Show live download / upload in the mini overlay.</summary>
    public bool OverlayShowActivity { get; set; } = true;
    /// <summary>Show a Windows notification when the connection drops, comes back, loses packets or switches network (see AlertPolicy).</summary>
    public bool DesktopAlerts { get; set; } = true;
    /// <summary>Only alert while Lagwise is not the active window (the page already shows the problem).</summary>
    public bool AlertsOnlyInBackground { get; set; } = true;
    /// <summary>Exported reports keep home-network and IPv6 addresses (a provider's support may ask for them). Off: they read [hidden].</summary>
    public bool ReportsIncludeAddresses { get; set; } = false;
    /// <summary>When the selected game starts, show the "Ready to play?" result as a desktop notification.</summary>
    public bool GameStartCheck { get; set; } = true;
    /// <summary>Show a small graph of the last minute of reference ping in the mini overlay.</summary>
    public bool OverlayShowGraph { get; set; } = true;

    // Site check
    /// <summary>Sites added by hand on the Site check page (host names).</summary>
    public List<string> SiteCheckHosts { get; set; } = new();
    /// <summary>Popular sites taken off the Site check page (their ids).</summary>
    public List<string> SiteCheckHidden { get; set; } = new();
    /// <summary>Check the sites every <see cref="SiteCheckEveryMinutes"/> minutes and note changes in the event timeline.</summary>
    public bool SiteCheckAuto { get; set; } = false;
    public int SiteCheckEveryMinutes { get; set; } = 15;

    // Appearance
    /// <summary>"Dark", "Light" or "System" (follow the Windows app theme).</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>Accent colour name (see the app's ThemeManager); unknown names fall back to the default.</summary>
    public string Accent { get; set; } = "Indigo";
    /// <summary>Interface size in percent (90-130).</summary>
    public int UiScalePercent { get; set; } = 100;

    // Mini overlay
    public bool OverlayVisible { get; set; } = false;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    /// <summary>Background opacity of the overlay, 40-100 percent.</summary>
    public int OverlayOpacityPercent { get; set; } = 85;
    /// <summary>Clicks on the mini overlay go to the window beneath it (the game); it is then moved and hidden from the title bar, tray or shortcut.</summary>
    public bool OverlayClickThrough { get; set; } = false;

    // Recordings
    /// <summary>Saved recordings older than this many days are deleted at start-up; 0 keeps them forever.</summary>
    public int RecordingRetentionDays { get; set; } = 0;

    // Configured targets
    public List<TargetConfig> Targets { get; set; } = new();

    public const int MaxSiteCheckHosts = 30;
    public const int MaxUserGames = 50;

    /// <summary>Clamps persisted values to safe ranges and restores defaults for anything unusable.</summary>
    public void Normalize()
    {
        MonitoringIntervalMs = System.Math.Clamp(MonitoringIntervalMs, 250, 60_000);
        HistoryWindowMinutes = System.Math.Clamp(HistoryWindowMinutes, 1, 240);
        RecentWindowSeconds = System.Math.Clamp(RecentWindowSeconds, 10, 600);
        EventDedupSeconds = System.Math.Clamp(EventDedupSeconds, 0, 600);
        PacketLossNotifyThreshold = System.Math.Clamp(PacketLossNotifyThreshold, 0.1, 100);
        HeavyTrafficMbps = System.Math.Clamp(HeavyTrafficMbps, 0, 10_000);
        SiteCheckEveryMinutes = System.Math.Clamp(SiteCheckEveryMinutes, 5, 1440);
        SiteCheckHidden ??= new List<string>();
        SiteCheckHosts = (SiteCheckHosts ?? new List<string>())
            .Select(Sites.SiteCatalog.NormalizeHost).Where(h => h != null).Select(h => h!).Distinct().Take(MaxSiteCheckHosts).ToList();
        LatencySpikeNotifyThreshold = System.Math.Clamp(LatencySpikeNotifyThreshold, 5, 10_000);
        RecordingRetentionDays = System.Math.Clamp(RecordingRetentionDays, 0, 3650);
        AutoRecordingsKept = System.Math.Clamp(AutoRecordingsKept, 0, 10_000);
        UserGames = (UserGames ?? new List<string>()).Where(Games.GameLibrary.IsUsableExePath)
            .Distinct(System.StringComparer.OrdinalIgnoreCase).Take(MaxUserGames).ToList();
        IgnoredGames = (IgnoredGames ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 100)
            .Distinct(System.StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        if (Theme is not ("Dark" or "Light" or "System")) Theme = "Dark";
        if (string.IsNullOrWhiteSpace(Accent)) Accent = "Indigo";
        UiScalePercent = System.Math.Clamp(UiScalePercent, 90, 130);
        OverlayOpacityPercent = System.Math.Clamp(OverlayOpacityPercent, 40, 100);
        if (string.IsNullOrWhiteSpace(TransferProviderId)) TransferProviderId = "cloudflare";
        if (!HotkeyGesture.TryParse(MarkLagHotkey, out _)) MarkLagHotkey = "Ctrl+Shift+F9";
        Targets ??= new List<TargetConfig>();
        if (Targets.Count == 0) Targets = CreateDefault().Targets;
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = System.Guid.NewGuid().ToString("N");
        }
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            MonitoringIntervalMs = 1000,
            HistoryWindowMinutes = 15,
            StartWithWindows = false,
            MinimizeToTray = true,
            CloseToTray = false,
            DarkTheme = true,
            NotifyOnDisconnect = true,
            NotifyOnPacketLoss = true,
            PacketLossNotifyThreshold = 5.0,
            NotifyOnLatencySpike = true,
            LatencySpikeNotifyThreshold = 100.0,
            NotifyOnRouteChange = true,
            IsBeginnerMode = true,
            Targets = new List<TargetConfig>
            {
                new TargetConfig
                {
                    Id = "gateway",
                    Name = "Local Gateway / Router",
                    Host = "auto",
                    Protocol = NetworkProtocol.Icmp,
                    IsEnabled = true,
                    IsGateway = true,
                    Description = "Primary default router hop connecting your PC to the Internet"
                },
                new TargetConfig
                {
                    Id = "cf-dns",
                    Name = "Cloudflare (1.1.1.1)",
                    Host = "1.1.1.1",
                    Protocol = NetworkProtocol.Icmp,
                    IsEnabled = true,
                    Description = "Global ultra-low latency anycast network"
                },
                new TargetConfig
                {
                    Id = "google-dns",
                    Name = "Google (8.8.8.8)",
                    Host = "8.8.8.8",
                    Protocol = NetworkProtocol.Icmp,
                    IsEnabled = true,
                    Description = "Global Tier-1 Google backbone resolver network"
                },
                new TargetConfig
                {
                    Id = "quad9-dns",
                    Name = "Quad9 (9.9.9.9)",
                    Host = "9.9.9.9",
                    Protocol = NetworkProtocol.Icmp,
                    IsEnabled = true,
                    Description = "Independent secure Swiss/global recursive resolver"
                },
                new TargetConfig
                {
                    Id = "dns-system",
                    Name = "DNS Resolution (System)",
                    Host = "cloudflare.com",
                    Protocol = NetworkProtocol.Dns,
                    IsEnabled = true,
                    IsDnsResolver = true,
                    Description = "Measures system DNS resolver latency resolving real domains"
                },
                new TargetConfig
                {
                    Id = "https-edge",
                    Name = "HTTPS Handshake (Cloudflare)",
                    Host = "cloudflare.com",
                    Port = 443,
                    Protocol = NetworkProtocol.Https,
                    IsEnabled = true,
                    Description = "Measures real-world TCP connect, TLS negotiation, and TTFB response latency"
                }
            }
        };
    }
}
