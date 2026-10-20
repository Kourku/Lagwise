using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Games;
using NetPulse.Core.Models;

namespace NetPulse.Core.Sessions;

/// <summary>
/// Records one monitoring session in memory: samples, events, monitoring gaps, disconnects and user lag markers.
/// All times are supplied by callers (or the injected clock) so recording is deterministic under test.
/// </summary>
public class SessionRecorder
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;
    private readonly int _maxSamples;

    private MonitoringSession? _session;
    private DateTime _lastCycleUtc;
    private int _intervalMs;
    private DisconnectRecord? _openDisconnect;

    /// <summary>Raised (outside the recorder lock) when a monitoring gap is detected.</summary>
    public event EventHandler<MonitoringGap>? GapDetected;

    public SessionRecorder(Func<DateTime>? clock = null, int maxSamples = 500_000)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _maxSamples = maxSamples;
    }

    public bool IsRecording
    {
        get { lock (_lock) return _session != null; }
    }

    public DateTime? StartedUtc
    {
        get { lock (_lock) return _session?.StartedUtc; }
    }

    /// <summary>The id of the recording in progress (its checkpoint file is already on disk), or null.</summary>
    public string? CurrentId
    {
        get { lock (_lock) return _session?.Id; }
    }

    public int SampleCount
    {
        get { lock (_lock) return _session?.Samples.Count ?? 0; }
    }

    public MonitoringSession Start(string? gameId, string? name, IEnumerable<TargetConfig> targets, NetworkInterfaceInfo? nic, int monitoringIntervalMs)
    {
        lock (_lock)
        {
            if (_session != null) throw new InvalidOperationException("A session is already being recorded.");

            var now = _clock();
            _intervalMs = Math.Max(250, monitoringIntervalMs);
            _lastCycleUtc = now;
            _openDisconnect = null;
            _session = new MonitoringSession
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Session {now.ToLocalTime():yyyy-MM-dd HH:mm}" : name.Trim(),
                AppVersion = AppInfo.Version,
                GameId = gameId,
                StartedUtc = now,
                MonitoringIntervalMs = _intervalMs,
                ActiveInterface = nic?.Name,
                InterfaceType = nic?.InterfaceType,
                Targets = targets.Select(t => new SessionTarget
                {
                    Id = t.Id,
                    Name = t.Name,
                    Host = t.Host,
                    Protocol = t.Protocol,
                    Kind = KindOf(t)
                }).ToList()
            };
            return _session;
        }
    }

    /// <summary>Ends the session and returns it (complete), or null when nothing was being recorded.</summary>
    public MonitoringSession? Stop()
    {
        lock (_lock)
        {
            var session = _session;
            if (session == null) return null;

            session.EndedUtc = _clock();
            session.IsComplete = true;
            _session = null;
            _openDisconnect = null;
            return session;
        }
    }

    /// <summary>Adds one second of this PC's activity (capped like samples: a day of seconds is plenty).</summary>
    /// <param name="cpuPercent">Processor load, percent of all cores; null when unknown.</param>
    /// <param name="wifiPercent">Wi-Fi signal while on Wi-Fi; null otherwise.</param>
    public void RecordThroughput(DateTime t, double rxBps, double txBps, double? cpuPercent = null, int? wifiPercent = null)
    {
        lock (_lock)
        {
            if (_session == null || _session.Throughput.Count >= 86_400) return;
            _session.Throughput.Add(new ThroughputSample
            {
                T = t, Rx = Math.Round(rxBps), Tx = Math.Round(txBps),
                Cpu = cpuPercent is double c ? Math.Round(Math.Clamp(c, 0, 100), 1) : null,
                Wifi = wifiPercent is int w ? Math.Clamp(w, 0, 100) : null
            });
        }
    }

    public void RecordMeasurement(Measurement m)
    {
        lock (_lock)
        {
            if (_session == null) return;
            if (_session.Samples.Count >= _maxSamples)
            {
                _session.SamplesTruncated = true;
                return;
            }

            _session.Samples.Add(new SessionSample
            {
                T = m.Timestamp,
                TargetId = m.TargetId,
                Ok = m.Success,
                RttMs = m.Success ? Math.Round(m.RttMs, 2) : null,
                Error = m.Success ? null : m.ErrorMessage
            });
        }
    }

    /// <summary>
    /// Called after every measurement cycle. A long silence since the previous cycle is recorded as a monitoring
    /// gap (NetPulse was not measuring), which must not be mistaken for a network outage.
    /// </summary>
    public void RecordCycle(DateTime cycleUtc)
    {
        MonitoringGap? gap = null;
        lock (_lock)
        {
            if (_session == null) return;

            var threshold = TimeSpan.FromMilliseconds(Math.Max(5000, _intervalMs * 3));
            if (cycleUtc - _lastCycleUtc > threshold)
            {
                gap = new MonitoringGap
                {
                    StartUtc = _lastCycleUtc,
                    EndUtc = cycleUtc,
                    Reason = "No measurements were taken (system sleep, stalled monitoring or the app was suspended). This is not evidence of a network outage."
                };
                _session.Gaps.Add(gap);
            }
            _lastCycleUtc = cycleUtc;
        }

        if (gap != null) GapDetected?.Invoke(this, gap);
    }

    public static MeasurementKind KindOf(TargetConfig t) =>
        t.Kind ?? (t.IsGateway ? MeasurementKind.LocalGateway : t.IsDnsResolver ? MeasurementKind.DnsResolution : MeasurementKind.ReferenceEndpoint);

    /// <summary>
    /// The game this recording is about. A recording started before any game ran gets its game (and its name) when the
    /// first one starts; a recording that already has a game keeps it.
    /// </summary>
    public void SetGameContext(GameProfile profile)
    {
        lock (_lock)
        {
            if (_session == null || (_session.GameId != null && _session.GameId != profile.Id)) return;
            bool placeholder = _session.GameId == null && _session.Name.StartsWith(WaitingName, StringComparison.Ordinal);
            _session.GameId = profile.Id;
            _session.GameDisplayName = profile.DisplayName;
            if (placeholder) _session.Name = profile.DisplayName + _session.Name[WaitingName.Length..];
        }
    }

    /// <summary>The start of a game recording's name until its game is known ("Game session 2026-10-20 20:05").</summary>
    public const string WaitingName = "Game session";

    /// <summary>The recording started by itself because a game started.</summary>
    public void MarkAutomatic()
    {
        lock (_lock)
        {
            if (_session != null) _session.Automatic = true;
        }
    }

    /// <summary>The recording is the Game Monitor's (its game may not be known yet).</summary>
    public void MarkGameRecording()
    {
        lock (_lock)
        {
            if (_session != null) _session.GameRecording = true;
        }
    }

    public void UpsertGameProcess(DetectedProcess p, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_session == null) return;
            var rec = _session.GameProcesses.FirstOrDefault(x => x.Pid == p.Pid && x.StartedUtc == p.StartTimeUtc && x.ExitedUtc == null);
            if (rec == null)
            {
                _session.GameProcesses.Add(new GameProcessRecord
                {
                    Pid = p.Pid, Name = p.Name, Role = p.Role.ToString(), StartedUtc = p.StartTimeUtc, FirstSeenUtc = nowUtc, LastSeenUtc = nowUtc
                });
            }
            else
            {
                rec.LastSeenUtc = nowUtc;
            }
        }
    }

    /// <summary>Marks recorded game processes that are no longer in <paramref name="stillRunning"/> as exited.</summary>
    public void MarkGameProcessesExited(IReadOnlySet<ProcessIdentity> stillRunning, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_session == null) return;
            foreach (var rec in _session.GameProcesses.Where(r => r.ExitedUtc == null && !stillRunning.Contains(new ProcessIdentity(r.Pid, r.StartedUtc))))
                rec.ExitedUtc = nowUtc;
        }
    }

    public void UpsertConnection(TrackedConnection c)
    {
        lock (_lock)
        {
            if (_session == null) return;
            var rec = _session.Connections.FirstOrDefault(x => x.Id == c.Id);
            if (rec == null)
            {
                rec = new ConnectionRecord
                {
                    Id = c.Id,
                    Protocol = c.Protocol.ToString().ToUpperInvariant(),
                    Local = c.LocalDisplay,
                    Remote = c.Remote == null ? null : c.RemoteDisplay,
                    Scope = c.Scope.ToString(),
                    Role = c.Role.ToString(),
                    Pid = c.Pid,
                    FirstSeenUtc = c.FirstSeenUtc
                };
                _session.Connections.Add(rec);
            }
            rec.State = c.State.ToString();
            rec.LastSeenUtc = c.LastSeenUtc;
            rec.EndedUtc = c.EndedUtc;
            rec.Pid = c.Pid;
        }
    }

    /// <summary>Records an event and tracks "Disconnected"/"Restored" pairs for the Internet scope as disconnect periods.</summary>
    public void RecordEvent(ConnectionEvent evt)
    {
        lock (_lock)
        {
            if (_session == null) return;
            _session.Events.Add(evt);

            if (evt.EventType == "Disconnected" && _openDisconnect == null)
            {
                _openDisconnect = new DisconnectRecord { StartUtc = evt.Timestamp };
                _session.Disconnects.Add(_openDisconnect);
            }
            else if (evt.EventType == "Restored" && _openDisconnect != null && evt.TargetName == ConnectivityScope)
            {
                _openDisconnect.EndUtc = evt.Timestamp;
                _openDisconnect = null;
            }
        }
    }

    public const string ConnectivityScope = "Internet";

    public void MarkLag(string? note = null, DateTime? timestamp = null)
    {
        lock (_lock)
        {
            if (_session == null) return;
            _session.LagMarkers.Add(new LagMarker { T = timestamp ?? _clock(), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
        }
    }

    /// <summary>An independent copy of the in-progress session (for checkpointing), or null when idle.</summary>
    public MonitoringSession? Snapshot()
    {
        lock (_lock)
        {
            if (_session == null) return null;
            var s = _session;
            return new MonitoringSession
            {
                SchemaVersion = s.SchemaVersion,
                Id = s.Id,
                Name = s.Name,
                AppVersion = s.AppVersion,
                GameId = s.GameId,
                Automatic = s.Automatic,
                GameRecording = s.GameRecording,
                StartedUtc = s.StartedUtc,
                EndedUtc = null,
                IsComplete = false,
                MonitoringIntervalMs = s.MonitoringIntervalMs,
                ActiveInterface = s.ActiveInterface,
                InterfaceType = s.InterfaceType,
                SamplesTruncated = s.SamplesTruncated,
                Targets = s.Targets.ToList(),
                Samples = s.Samples.ToList(),
                Events = s.Events.ToList(),
                Gaps = s.Gaps.ToList(),
                Disconnects = s.Disconnects.Select(d => new DisconnectRecord { StartUtc = d.StartUtc, EndUtc = d.EndUtc }).ToList(),
                LagMarkers = s.LagMarkers.ToList(),
                Throughput = s.Throughput.ToList(),
                GameDisplayName = s.GameDisplayName,
                GameProcesses = s.GameProcesses.Select(g => new GameProcessRecord { Pid = g.Pid, Name = g.Name, Role = g.Role, StartedUtc = g.StartedUtc, FirstSeenUtc = g.FirstSeenUtc, LastSeenUtc = g.LastSeenUtc, ExitedUtc = g.ExitedUtc }).ToList(),
                Connections = s.Connections.Select(c => new ConnectionRecord { Id = c.Id, Protocol = c.Protocol, Local = c.Local, Remote = c.Remote, Scope = c.Scope, Role = c.Role, State = c.State, Pid = c.Pid, FirstSeenUtc = c.FirstSeenUtc, LastSeenUtc = c.LastSeenUtc, EndedUtc = c.EndedUtc }).ToList()
            };
        }
    }
}
