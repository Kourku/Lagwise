using System;
using System.Collections.Generic;
using NetPulse.Core.Models;

namespace NetPulse.Core.Sessions;

/// <summary>
/// What a recorded target represents. Only <see cref="GameServer"/> may ever be presented as game-server latency,
/// and nothing records that kind until a real game endpoint has been identified.
/// </summary>
public enum MeasurementKind
{
    LocalGateway,
    DnsResolution,
    /// <summary>A public reference endpoint (DNS anycast, CDN). Says nothing about a specific game server.</summary>
    ReferenceEndpoint,
    /// <summary>Recordings made before 1.0 only: an endpoint seen on the game process and measured at the user's request.</summary>
    GameProcessEndpoint,
    GameServer
}

public class SessionTarget
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public NetworkProtocol Protocol { get; set; }
    public MeasurementKind Kind { get; set; }
}

public class SessionSample
{
    public DateTime T { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public bool Ok { get; set; }
    /// <summary>Round-trip time in milliseconds; null when the probe failed.</summary>
    public double? RttMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>One second of this PC's activity while recording: traffic on the adapter Windows uses for the internet, and more.</summary>
public class ThroughputSample
{
    public DateTime T { get; set; }
    /// <summary>Download, bits per second.</summary>
    public double Rx { get; set; }
    /// <summary>Upload, bits per second.</summary>
    public double Tx { get; set; }
    /// <summary>How busy the processor was, percent of all cores (recordings from 1.0 on).</summary>
    public double? Cpu { get; set; }
    /// <summary>Wi-Fi signal in percent while on Wi-Fi (recordings from 1.0 on).</summary>
    public int? Wifi { get; set; }
}

/// <summary>A period during which NetPulse itself was not measuring (sleep, stalled engine). Not a network failure.</summary>
public class MonitoringGap
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public double DurationSeconds => (EndUtc - StartUtc).TotalSeconds;
}

/// <summary>A period during which no probe to any target succeeded.</summary>
public class DisconnectRecord
{
    public DateTime StartUtc { get; set; }
    /// <summary>Null while still ongoing (or when the session ended during the outage).</summary>
    public DateTime? EndUtc { get; set; }
    public double? DurationSeconds => EndUtc.HasValue ? (EndUtc.Value - StartUtc).TotalSeconds : null;
}

/// <summary>A moment the user reported lag ("it just stuttered"), for correlation with measured events.</summary>
public class LagMarker
{
    public DateTime T { get; set; }
    public string? Note { get; set; }
}

/// <summary>A socket owned by the game process: whether the game was online, and how (the match server itself is never known).</summary>
public class ConnectionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Local { get; set; } = string.Empty;
    /// <summary>Null for UDP sockets and listeners (Windows does not expose their remote peer).</summary>
    public string? Remote { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Role { get; set; } = "Unknown";
    public string State { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

public class GameProcessRecord
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime? StartedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? ExitedUtc { get; set; }
}

public class MonitoringSession
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    /// <summary>NetPulse version that recorded the session (stored findings reflect that version's analysis). Null for recordings made before 0.4.</summary>
    public string? AppVersion { get; set; }
    /// <summary>The recorded game's id, or null for a network recording (or a game recording before its game started).</summary>
    public string? GameId { get; set; }
    /// <summary>Started by itself because a game started (automatic recordings are pruned to the newest ones).</summary>
    public bool Automatic { get; set; }
    /// <summary>Made by the Game Monitor (also one that waited for a game that never started).</summary>
    public bool GameRecording { get; set; }

    /// <summary>A Game Monitor recording, whether or not its game was known (recordings before 1.0 only have <see cref="GameId"/>).</summary>
    public bool IsGameRecording => GameRecording || GameId != null;
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    /// <summary>False for a checkpoint written during recording (or after a crash), true once the session was stopped normally.</summary>
    public bool IsComplete { get; set; }
    public int MonitoringIntervalMs { get; set; }
    public string? ActiveInterface { get; set; }
    public string? InterfaceType { get; set; }

    public List<SessionTarget> Targets { get; set; } = new();
    public List<SessionSample> Samples { get; set; } = new();
    public List<ConnectionEvent> Events { get; set; } = new();
    public List<MonitoringGap> Gaps { get; set; } = new();
    public List<DisconnectRecord> Disconnects { get; set; } = new();
    public List<LagMarker> LagMarkers { get; set; } = new();
    /// <summary>Traffic on this PC during the recording (empty for recordings made before 0.13).</summary>
    public List<ThroughputSample> Throughput { get; set; } = new();

    // ---- Game monitoring context (empty for plain network sessions) ----
    public string? GameDisplayName { get; set; }
    public List<GameProcessRecord> GameProcesses { get; set; } = new();
    public List<ConnectionRecord> Connections { get; set; } = new();

    /// <summary>True when the sample cap was hit and later samples were dropped.</summary>
    public bool SamplesTruncated { get; set; }
}

public class SessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GameId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public bool IsComplete { get; set; }
    public int SampleCount { get; set; }
    public int EventCount { get; set; }
    public int LagMarkerCount { get; set; }
    public string? GameDisplayName { get; set; }
    public string? AppVersion { get; set; }
    public bool Automatic { get; set; }
    public bool IsGameRecording { get; set; }
    /// <summary>The recording's verdict (see <see cref="SessionVerdict"/>), worked out when it was saved or indexed.</summary>
    public VerdictLevel? Verdict { get; set; }
    public string? VerdictHeadline { get; set; }
    /// <summary>Size of the file on disk.</summary>
    public long SizeBytes { get; set; }

    public static SessionSummary From(MonitoringSession s, long sizeBytes) => new SessionSummary
    {
        Id = s.Id,
        Name = s.Name,
        GameId = s.GameId,
        StartedUtc = s.StartedUtc,
        EndedUtc = s.EndedUtc,
        IsComplete = s.IsComplete,
        SampleCount = s.Samples.Count,
        EventCount = s.Events.Count,
        LagMarkerCount = s.LagMarkers.Count,
        GameDisplayName = s.GameDisplayName,
        AppVersion = s.AppVersion,
        Automatic = s.Automatic,
        IsGameRecording = s.IsGameRecording,
        SizeBytes = sizeBytes
    }.WithVerdict(s);

    private SessionSummary WithVerdict(MonitoringSession s)
    {
        try
        {
            var v = SessionVerdict.Build(s);
            Verdict = v.Level;
            VerdictHeadline = v.Headline;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // an odd recording keeps its other details
        }
        return this;
    }
}
