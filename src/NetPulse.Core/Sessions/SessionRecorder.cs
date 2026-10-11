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

    /// <summary>Adds a measurement target that appeared mid-session (e.g. an endpoint the user chose to measure). Idempotent.</summary>
    public void RecordTarget(TargetConfig t)
    {
        lock (_lock)
        {
            if (_session == null || _session.Targets.Any(x => x.Id == t.Id)) return;
            _session.Targets.Add(new SessionTarget { Id = t.Id, Name = t.Name, Host = t.Host, Protocol = t.Protocol, Kind = KindOf(t) });
        }
    }

    public void SetGameContext(GameProfile profile)
    {
        lock (_lock)
        {
            if (_session == null) return;
            _session.GameDisplayName = profile.DisplayName;
            _session.GameSupportStatus = profile.Status.ToString();
            _session.Limitations = profile.Limitations.ToList();
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

    public void SetFindings(IEnumerable<GameFinding> findings)
    {
        lock (_lock)
        {
            if (_session == null) return;
            _session.Findings = findings.ToList();
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
                GameId = s.GameId,
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
                GameDisplayName = s.GameDisplayName,
                GameSupportStatus = s.GameSupportStatus,
                Limitations = s.Limitations.ToList(),
                GameProcesses = s.GameProcesses.Select(g => new GameProcessRecord { Pid = g.Pid, Name = g.Name, Role = g.Role, StartedUtc = g.StartedUtc, FirstSeenUtc = g.FirstSeenUtc, LastSeenUtc = g.LastSeenUtc, ExitedUtc = g.ExitedUtc }).ToList(),
                Connections = s.Connections.Select(c => new ConnectionRecord { Id = c.Id, Protocol = c.Protocol, Local = c.Local, Remote = c.Remote, Scope = c.Scope, Role = c.Role, State = c.State, Pid = c.Pid, FirstSeenUtc = c.FirstSeenUtc, LastSeenUtc = c.LastSeenUtc, EndedUtc = c.EndedUtc }).ToList(),
                Findings = s.Findings.ToList()
            };
        }
    }
}
