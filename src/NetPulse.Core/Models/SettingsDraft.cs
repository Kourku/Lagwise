using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Models;

public sealed class TargetDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public NetworkProtocol Protocol { get; set; } = NetworkProtocol.Icmp;
    public int Port { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsGateway { get; set; }
    public bool IsDnsResolver { get; set; }
    /// <summary>Targets NetPulse ships with can be switched off but not deleted; only user-added targets can be removed.</summary>
    public bool IsBuiltIn { get; set; }

    public string MethodLabel => Protocol switch
    {
        NetworkProtocol.Icmp => "Ping (ICMP)",
        NetworkProtocol.Tcp => $"TCP connect, port {Port}",
        NetworkProtocol.Https => "HTTPS handshake",
        NetworkProtocol.Dns => "DNS lookup",
        _ => Protocol.ToString()
    };
}

public static class TargetValidator
{
    public const int MaxTargets = 12;

    /// <summary>Returns an error message for a user-entered target, or null when it is acceptable.</summary>
    public static string? Validate(string? name, string? host, NetworkProtocol protocol, string? portText, IEnumerable<TargetDraft> existing, out int port)
    {
        port = 0;
        name = name?.Trim() ?? string.Empty;
        host = host?.Trim() ?? string.Empty;
        var list = existing.ToList();

        if (name.Length == 0) return "Give the target a name.";
        if (name.Length > 40) return "Keep the name under 40 characters.";
        if (host.Length == 0) return "Enter an address or host name, for example 1.1.1.1 or example.com.";
        if (host.Contains("://") || host.Contains('/') || host.Any(char.IsWhiteSpace))
            return "Enter just the address or host name, without http://, a path or spaces.";
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
            return "That does not look like a valid IP address or host name.";

        if (protocol is NetworkProtocol.Tcp or NetworkProtocol.Https)
        {
            string text = string.IsNullOrWhiteSpace(portText) ? "443" : portText.Trim();
            if (!int.TryParse(text, out port) || port < 1 || port > 65535) return "The port must be a number from 1 to 65535.";
        }

        if (list.Count >= MaxTargets) return $"NetPulse monitors at most {MaxTargets} targets so probing stays light. Remove one first.";
        int chosenPort = port;
        if (list.Any(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) && t.Protocol == protocol && t.Port == chosenPort))
            return "That target is already in the list.";
        return null;
    }
}

/// <summary>
/// An editable copy of the user-changeable settings. The Settings screen edits a draft; nothing changes until it is validated and applied,
/// so a half-typed value can never reach the running measurement engine.
/// </summary>
public sealed class SettingsDraft
{
    public int ProbeIntervalMs { get; set; }
    public int RecentWindowSeconds { get; set; }
    public int HistoryWindowMinutes { get; set; }
    public int EventDedupSeconds { get; set; }
    public bool NotifyOnLatencySpike { get; set; }
    public double LatencySpikeThresholdMs { get; set; }
    public bool NotifyOnPacketLoss { get; set; }
    public double PacketLossThresholdPercent { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public string MarkLagHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool MarkLagSound { get; set; }
    public int HeavyTrafficMbps { get; set; }
    public bool OverlayShowActivity { get; set; }
    public bool DesktopAlerts { get; set; }
    public bool AlertsOnlyInBackground { get; set; }
    public int RecordingRetentionDays { get; set; }
    public List<TargetDraft> Targets { get; set; } = new();

    private static readonly HashSet<string> BuiltInIds = AppSettings.CreateDefault().Targets.Select(t => t.Id).ToHashSet();

    public static SettingsDraft FromSettings(AppSettings s, bool startWithWindows) => new()
    {
        ProbeIntervalMs = s.MonitoringIntervalMs,
        RecentWindowSeconds = s.RecentWindowSeconds,
        HistoryWindowMinutes = s.HistoryWindowMinutes,
        EventDedupSeconds = s.EventDedupSeconds,
        NotifyOnLatencySpike = s.NotifyOnLatencySpike,
        LatencySpikeThresholdMs = s.LatencySpikeNotifyThreshold,
        NotifyOnPacketLoss = s.NotifyOnPacketLoss,
        PacketLossThresholdPercent = s.PacketLossNotifyThreshold,
        MinimizeToTray = s.MinimizeToTray,
        CloseToTray = s.CloseToTray,
        StartWithWindows = startWithWindows,
        MarkLagHotkey = s.MarkLagHotkey,
        MarkLagSound = s.MarkLagSound,
        HeavyTrafficMbps = s.HeavyTrafficMbps,
        OverlayShowActivity = s.OverlayShowActivity,
        DesktopAlerts = s.DesktopAlerts,
        AlertsOnlyInBackground = s.AlertsOnlyInBackground,
        RecordingRetentionDays = s.RecordingRetentionDays,
        Targets = s.Targets.Select(t => new TargetDraft
        {
            Id = t.Id, Name = t.Name, Host = t.Host, Protocol = t.Protocol, Port = t.Port, Enabled = t.IsEnabled,
            IsGateway = t.IsGateway, IsDnsResolver = t.IsDnsResolver, IsBuiltIn = BuiltInIds.Contains(t.Id)
        }).ToList()
    };

    public static List<TargetDraft> DefaultTargets() => FromSettings(AppSettings.CreateDefault(), false).Targets;

    /// <summary>Messages describing every problem that would stop the draft from being applied (empty = valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (LatencySpikeThresholdMs < 5 || LatencySpikeThresholdMs > 10_000) errors.Add("The latency spike limit must be between 5 and 10000 ms.");
        if (PacketLossThresholdPercent < 0.1 || PacketLossThresholdPercent > 100) errors.Add("The packet loss limit must be between 0.1 and 100 %.");
        if (!Targets.Any(t => t.Enabled && !t.IsGateway && !t.IsDnsResolver)) errors.Add("Keep at least one reference server enabled; without one NetPulse has nothing to compare your router against.");
        if (!HotkeyGesture.TryParse(MarkLagHotkey, out _)) errors.Add("The Mark lag shortcut is not a valid key combination.");
        if (RecordingRetentionDays < 0 || RecordingRetentionDays > 3650) errors.Add("Recordings can be kept for at most 10 years.");
        if (Targets.Count > TargetValidator.MaxTargets) errors.Add($"At most {TargetValidator.MaxTargets} targets can be monitored.");
        return errors;
    }

    /// <summary>
    /// Copies the draft into <paramref name="settings"/>. Existing target objects are reused (the engine writes the discovered router
    /// address into the gateway target, which must survive), and the new list replaces the old one in a single assignment.
    /// </summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.MonitoringIntervalMs = ProbeIntervalMs;
        settings.RecentWindowSeconds = RecentWindowSeconds;
        settings.HistoryWindowMinutes = HistoryWindowMinutes;
        settings.EventDedupSeconds = EventDedupSeconds;
        settings.NotifyOnLatencySpike = NotifyOnLatencySpike;
        settings.LatencySpikeNotifyThreshold = LatencySpikeThresholdMs;
        settings.NotifyOnPacketLoss = NotifyOnPacketLoss;
        settings.PacketLossNotifyThreshold = PacketLossThresholdPercent;
        settings.MinimizeToTray = MinimizeToTray;
        settings.CloseToTray = CloseToTray;
        settings.StartWithWindows = StartWithWindows;
        settings.MarkLagHotkey = MarkLagHotkey;
        settings.MarkLagSound = MarkLagSound;
        settings.HeavyTrafficMbps = HeavyTrafficMbps;
        settings.OverlayShowActivity = OverlayShowActivity;
        settings.DesktopAlerts = DesktopAlerts;
        settings.AlertsOnlyInBackground = AlertsOnlyInBackground;
        settings.RecordingRetentionDays = RecordingRetentionDays;

        var existing = settings.Targets.ToDictionary(t => t.Id);
        var merged = new List<TargetConfig>();
        foreach (var d in Targets)
        {
            if (!existing.TryGetValue(d.Id, out var t))
            {
                t = new TargetConfig { Id = d.Id, Name = d.Name, Host = d.Host, Protocol = d.Protocol, Port = d.Port, Kind = MeasurementKind.ReferenceEndpoint };
            }
            t.IsEnabled = d.Enabled;
            if (!d.IsGateway && !d.IsBuiltIn)
            {
                t.Name = d.Name;
                t.Host = d.Host;
                t.Protocol = d.Protocol;
                t.Port = d.Port;
            }
            merged.Add(t);
        }

        settings.Targets = merged;
        settings.Normalize();
    }
}
