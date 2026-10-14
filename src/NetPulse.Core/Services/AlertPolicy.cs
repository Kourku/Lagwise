using System;
using System.Collections.Generic;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Services;

/// <summary>A desktop notification worth showing: short enough for a Windows notification.</summary>
public sealed record DesktopAlert(string Title, string Message, bool IsProblem);

/// <summary>
/// Decides which logged events deserve a desktop notification. Only a handful do: losing the connection (and getting it
/// back), the router going silent, sustained probe loss and Windows switching networks. Spikes, route changes and Wi-Fi
/// roaming stay in the timeline only. Each kind of alert is shown at most once per <see cref="Cooldown"/>, and nothing is
/// shown during the first <see cref="WarmUp"/> after start, while baselines settle.
/// </summary>
public sealed class AlertPolicy
{
    private readonly Dictionary<string, DateTime> _lastShown = new();
    private readonly DateTime _startedUtc;
    private bool _toldDisconnected;

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan WarmUp { get; set; } = TimeSpan.FromSeconds(45);

    public AlertPolicy(DateTime startedUtc) => _startedUtc = startedUtc;

    public DesktopAlert? Evaluate(ConnectionEvent e)
    {
        var now = e.Timestamp;
        switch (e.EventType)
        {
            case "Disconnected":
                if (!Allowed("down", now)) return null;
                _toldDisconnected = true;
                return new DesktopAlert("Connection lost", "Nothing on the Internet is answering. NetPulse will tell you when it is back.", true);

            case "Restored" when e.TargetName == SessionRecorder.ConnectivityScope:
                if (!_toldDisconnected) return null;   // only close an alert we actually opened
                _toldDisconnected = false;
                return new DesktopAlert("Back online", Trim(e.Description), false);

            case "GatewayDegraded":
                return Allowed("router", now)
                    ? new DesktopAlert("Your router stopped answering", "The problem is likely inside your home network (router, cable or Wi-Fi).", true)
                    : null;

            case "PacketLoss":
                return Allowed("loss", now)
                    ? new DesktopAlert("Packets are being lost", $"{e.TargetName}: {(e.MetricValue is double v ? $"{v:F0}% of probes" : "probes")} lost in the last minute. Games may stutter or rubber-band.", true)
                    : null;

            case "NetworkChange" when e.Severity != "Info":
                return Allowed("network", now)
                    ? new DesktopAlert("Your network changed", Trim(e.Description), true)
                    : null;

            default:
                return null;
        }
    }

    private bool Allowed(string kind, DateTime now)
    {
        if (now - _startedUtc < WarmUp) return false;
        if (_lastShown.TryGetValue(kind, out var last) && now - last < Cooldown) return false;
        _lastShown[kind] = now;
        return true;
    }

    /// <summary>Windows cuts notification text at about 200 characters.</summary>
    private static string Trim(string s) => s.Length <= 200 ? s : s[..199] + "…";
}
