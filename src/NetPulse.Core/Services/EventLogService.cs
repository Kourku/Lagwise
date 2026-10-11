using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;

namespace NetPulse.Core.Services;

public class EventLogService
{
    /// <summary>Event types that recur while a condition persists and are therefore collapsed within the dedup window.</summary>
    private static readonly HashSet<string> DedupedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LatencySpike", "PacketLoss", "GatewayDegraded", "DnsFailure"
    };

    private readonly List<ConnectionEvent> _events = new();
    private readonly Dictionary<string, (DateTime LastLogged, int Suppressed)> _recent = new();
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;

    public event EventHandler<ConnectionEvent>? EventLogged;

    /// <summary>Recurring events with the same type and target inside this period are suppressed.</summary>
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromSeconds(30);

    public EventLogService(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Logs an event. Returns false when it was suppressed as a recurrence of a recent identical event.</summary>
    public bool LogEvent(string eventType, string severity, string description, string targetName = "", double? metricValue = null, string? contextData = null, DateTime? timestamp = null)
    {
        var now = timestamp ?? _clock();
        ConnectionEvent evt;

        lock (_lock)
        {
            if (DedupWindow > TimeSpan.Zero && DedupedTypes.Contains(eventType))
            {
                string key = eventType + "|" + targetName;
                if (_recent.TryGetValue(key, out var last) && now - last.LastLogged < DedupWindow)
                {
                    _recent[key] = (last.LastLogged, last.Suppressed + 1);
                    return false;
                }

                int suppressed = _recent.TryGetValue(key, out var prev) ? prev.Suppressed : 0;
                _recent[key] = (now, 0);
                if (suppressed > 0) description += $" ({suppressed} similar event{(suppressed == 1 ? "" : "s")} suppressed since the previous entry)";
            }

            evt = new ConnectionEvent
            {
                Timestamp = now,
                EventType = eventType,
                Severity = severity,
                Description = description,
                TargetName = targetName,
                MetricValue = metricValue,
                ContextData = contextData
            };

            _events.Insert(0, evt);
            if (_events.Count > 1000)
            {
                _events.RemoveAt(_events.Count - 1);
            }
        }

        EventLogged?.Invoke(this, evt);
        return true;
    }

    public IReadOnlyList<ConnectionEvent> GetRecentEvents(int maxCount = 100, string? filterSeverity = null)
    {
        lock (_lock)
        {
            var query = _events.AsEnumerable();
            if (!string.IsNullOrEmpty(filterSeverity) && !filterSeverity.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(e => e.Severity.Equals(filterSeverity, StringComparison.OrdinalIgnoreCase));
            }
            return query.Take(maxCount).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
            _recent.Clear();
        }
    }
}
