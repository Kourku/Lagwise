using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core.Models;
using NetPulse.Core.Sessions;

namespace NetPulse.Core.Games;

public enum FindingConfidence { Low, Medium, High }
public enum FindingSeverity { Info, Notice, Warning }

/// <summary>
/// A diagnostic conclusion that separates what was observed from what it might mean.
/// Observations are facts from measurements; explanations are possibilities, never conclusions.
/// </summary>
public sealed class GameFinding
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public FindingSeverity Severity { get; set; }
    public FindingConfidence Confidence { get; set; }
    public List<string> Observations { get; set; } = new();
    public List<string> PossibleExplanations { get; set; } = new();
    /// <summary>Independent measurements that support or limit the finding.</summary>
    public List<string> Evidence { get; set; } = new();
    /// <summary>Measurements that point the other way; they lower confidence.</summary>
    public List<string> Contradictions { get; set; } = new();
    public string NextTest { get; set; } = string.Empty;
}

public sealed class GameDiagnosticInput
{
    public required IReadOnlyList<TargetMetrics> Metrics { get; init; }
    public GameProcessState? Process { get; init; }
    public IReadOnlyList<TrackedConnection> ActiveConnections { get; init; } = Array.Empty<TrackedConnection>();
    public IReadOnlyList<ConnectionChange> RecentChanges { get; init; } = Array.Empty<ConnectionChange>();
    public int RecentWindowSeconds { get; init; } = 60;
}

/// <summary>
/// Correlates independent measurements (router, several reference endpoints, user-selected game-process endpoints,
/// connection changes) before suggesting a cause. It never states a cause as fact: every finding carries a confidence
/// derived from how many independent measurements agree and disagree, plus the next test that would narrow it down.
/// </summary>
public static class GameDiagnosticAnalyzer
{
    /// <summary>Probes needed in the recent window before a target's statistics are trusted.</summary>
    public const int MinSamples = 8;
    private const double LossThreshold = 5.0;

    public static bool IsDegraded(TargetMetrics m, out string reason)
    {
        reason = string.Empty;
        if (m.RecentSampleCount < MinSamples) return false;

        if (m.PacketLossPercent >= LossThreshold)
        {
            reason = $"{m.PacketLossPercent:F0}% probe loss over the last {m.RecentWindowSeconds:F0} s ({m.RecentSampleCount} probes)";
            return true;
        }
        if (m.IsLatencyElevated)
        {
            reason = $"latency {m.CurrentLatency:F0} ms versus its own typical {m.BaselineLatencyMs:F0} ms";
            return true;
        }
        double jitterLimit = m.BaselineLatencyMs > 0 ? Math.Max(25, m.BaselineLatencyMs * 0.5) : 25;
        if (m.Jitter >= jitterLimit)
        {
            reason = $"latency variation {m.Jitter:F0} ms over the last {m.RecentWindowSeconds:F0} s";
            return true;
        }
        return false;
    }

    /// <summary>
    /// A target that has been unsteady for most of the monitoring period (persistent probe loss, or a long tail of latency spikes).
    /// Its current misbehaviour is its normal state, so it is not counted as independent evidence of a *new* problem.
    /// Typical causes: public resolvers rate-limiting ICMP, or HTTPS handshakes whose time includes TLS and server processing.
    /// </summary>
    public static bool IsChronicallyNoisy(TargetMetrics m, out string reason)
    {
        reason = string.Empty;
        if (m.TotalProbes >= 300 && m.SessionPacketLossPercent >= 3.0)
        {
            reason = $"{m.SessionPacketLossPercent:F1}% of {m.TotalProbes} probes unanswered since monitoring began";
            return true;
        }

        var history = m.RecentHistory;
        if (history.Count >= 100 && m.MedianLatency > 0 && m.P95Latency > 3.0 * m.MedianLatency)
        {
            reason = $"95th-percentile latency {m.P95Latency:F0} ms versus a median of {m.MedianLatency:F0} ms over the recent history";
            return true;
        }
        return false;
    }

    private static bool IsStable(TargetMetrics m) =>
        m.RecentSampleCount >= MinSamples && m.PacketLossPercent < 1.0 && !IsDegraded(m, out _) && m.IsReachable;

    public static IReadOnlyList<GameFinding> Analyze(GameDiagnosticInput input)
    {
        var findings = new List<GameFinding>();
        var gateway = input.Metrics.FirstOrDefault(m => m.Kind == MeasurementKind.LocalGateway);
        var references = input.Metrics.Where(m => m.Kind == MeasurementKind.ReferenceEndpoint).ToList();
        var endpoints = input.Metrics.Where(m => m.IsGameEndpoint).ToList();

        var noisyRefs = references.Where(r => r.RecentSampleCount >= MinSamples && IsChronicallyNoisy(r, out _)).ToList();
        var refsWithData = references.Where(r => r.RecentSampleCount >= MinSamples && !noisyRefs.Contains(r)).ToList();
        var degradedRefs = refsWithData.Where(r => IsDegraded(r, out _)).ToList();
        bool gatewayHasData = gateway != null && gateway.RecentSampleCount >= MinSamples;
        bool gatewayDegraded = gatewayHasData && IsDegraded(gateway!, out _);
        bool gatewayStable = gatewayHasData && IsStable(gateway!);

        if (!gatewayHasData && refsWithData.Count == 0)
        {
            findings.Add(new GameFinding
            {
                Id = "insufficient-data",
                Title = "Not enough recent measurements yet",
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.Low,
                Observations = { $"Fewer than {MinSamples} probes have completed in the recent window for the router and reference targets." },
                PossibleExplanations = { "Monitoring has only just started, or the app was suspended." },
                NextTest = "Keep monitoring for another minute."
            });
        }

        if (noisyRefs.Count > 0)
        {
            var f = new GameFinding
            {
                Id = "noisy-references",
                Title = noisyRefs.Count == 1 ? $"{noisyRefs[0].TargetName} is a consistently unsteady reference" : $"{noisyRefs.Count} reference targets are consistently unsteady",
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.Medium,
                PossibleExplanations =
                {
                    "Public DNS resolvers often rate-limit or deprioritise ICMP replies, which looks like packet loss without affecting real traffic.",
                    "An HTTPS handshake includes TLS negotiation and server processing time, so it varies far more than a plain ping.",
                    "A persistent path problem to that one target (not shown to affect your router or the steadier targets)."
                },
                NextTest = "Judge the connection mainly by your router and the steadier references; use Mark lag to see whether the noisy ones actually move when you feel lag."
            };
            foreach (var r in noisyRefs)
            {
                IsChronicallyNoisy(r, out var why);
                f.Observations.Add($"{r.TargetName}: {why}.");
                f.Evidence.Add($"{r.TargetName}: unsteady for the whole period, so it is not counted as evidence of a new problem.");
            }
            findings.Add(f);
        }

        AddLocalVersusUpstream(findings, gateway, gatewayDegraded, gatewayStable, refsWithData, degradedRefs, endpoints);
        AddEndpointFindings(findings, endpoints, gatewayStable, refsWithData, degradedRefs, input);
        AddConnectionChangeFinding(findings, input, degradedRefs.Count > 0 || gatewayDegraded);

        if (input.Process != null && input.Process.State != GameRunState.Running)
        {
            findings.Add(new GameFinding
            {
                Id = "game-not-running",
                Title = "The game itself is not running",
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.High,
                Observations = { input.Process.Message },
                PossibleExplanations = { "Measurements currently describe your general connection only; there are no game connections to compare against." },
                NextTest = "Start the game and enter a match, then check again."
            });
        }

        if (findings.Count == 0 || findings.All(f => f.Severity == FindingSeverity.Info))
        {
            if (gatewayHasData || refsWithData.Count > 0)
            {
                findings.Insert(0, new GameFinding
                {
                    Id = "no-problem-observed",
                    Title = "No problem observed in the recent window",
                    Severity = FindingSeverity.Info,
                    Confidence = refsWithData.Count >= 2 && gatewayHasData ? FindingConfidence.Medium : FindingConfidence.Low,
                    Observations = { $"Router and reference targets showed no loss or unusual latency over the last {input.RecentWindowSeconds} s." },
                    PossibleExplanations = { "If you still experienced lag, the cause may be on the game's servers or in a path these targets do not share. In-game latency is not measured by NetPulse." },
                    NextTest = "Press Mark lag when it happens, so the timeline can be checked at that moment."
                });
            }
        }

        return findings;
    }

    private static void AddLocalVersusUpstream(List<GameFinding> findings, TargetMetrics? gateway, bool gatewayDegraded, bool gatewayStable,
        List<TargetMetrics> refsWithData, List<TargetMetrics> degradedRefs, List<TargetMetrics> endpoints)
    {
        // Reference targets are independent providers, so agreement between them is stronger evidence than any single one.
        var stableRefs = refsWithData.Except(degradedRefs).ToList();

        if (gatewayDegraded && degradedRefs.Count >= 2)
        {
            IsDegraded(gateway!, out var gwReason);
            var f = new GameFinding
            {
                Id = "local-network",
                Title = "Possible local network problem",
                Severity = FindingSeverity.Warning,
                Confidence = degradedRefs.Count >= 3 ? FindingConfidence.High : FindingConfidence.Medium,
                Observations =
                {
                    $"Your router showed {gwReason}.",
                    $"{degradedRefs.Count} of {refsWithData.Count} independent reference targets degraded in the same window."
                },
                PossibleExplanations =
                {
                    "Wi-Fi interference or weak signal, a congested or faulty local link, or a router under load.",
                    "A heavy upload/download on this PC or another device (see the Bufferbloat test)."
                },
                NextTest = "Test with an Ethernet cable if on Wi-Fi, pause other transfers, then run the Bufferbloat test."
            };
            f.Evidence.Add($"Router: {gwReason}");
            foreach (var r in degradedRefs) { IsDegraded(r, out var why); f.Evidence.Add($"{r.TargetName}: {why}"); }
            foreach (var r in stableRefs) f.Contradictions.Add($"{r.TargetName} stayed normal.");
            if (stableRefs.Count > 0) f.Confidence = Lower(f.Confidence);
            findings.Add(f);
        }
        else if (gatewayDegraded)
        {
            IsDegraded(gateway!, out var gwReason);
            findings.Add(new GameFinding
            {
                Id = "gateway-only",
                Title = "Router measurements look unsteady, but nothing beyond it does",
                Severity = FindingSeverity.Notice,
                Confidence = FindingConfidence.Low,
                Observations = { $"Router: {gwReason}.", refsWithData.Count > 0 ? "All reference targets stayed normal." : "No reference target has enough data yet." },
                PossibleExplanations =
                {
                    "Routers often deprioritise replies to their own address, which can look worse than the traffic actually passing through.",
                    "A brief local Wi-Fi disturbance."
                },
                Evidence = { $"Router: {gwReason}" },
                Contradictions = refsWithData.Select(r => $"{r.TargetName} stayed normal.").ToList(),
                NextTest = "Only act on this if you also feel lag; mark the moment with Mark lag and compare timestamps."
            });
        }
        else if (gatewayStable && degradedRefs.Count >= 2)
        {
            var f = new GameFinding
            {
                Id = "beyond-local",
                Title = "Possible problem beyond your local network",
                Severity = FindingSeverity.Warning,
                Confidence = degradedRefs.Count >= 3 ? FindingConfidence.High : FindingConfidence.Medium,
                Observations =
                {
                    "Your router responded normally.",
                    $"{degradedRefs.Count} of {refsWithData.Count} independent reference targets degraded at the same time."
                },
                PossibleExplanations =
                {
                    "Congestion or a fault at your ISP or along the route to these targets.",
                    "Problems inside the reference providers' own networks (less likely when several unrelated providers are affected).",
                    "Your own link being saturated, which a router ping may not show (see the Bufferbloat test)."
                },
                NextTest = "Run an MTR/route trace to an affected target and a Bufferbloat test; compare when the problem started with your ISP's status."
            };
            f.Evidence.Add("Router: stable (no loss, no elevated latency).");
            foreach (var r in degradedRefs) { IsDegraded(r, out var why); f.Evidence.Add($"{r.TargetName}: {why}"); }
            foreach (var r in stableRefs) f.Contradictions.Add($"{r.TargetName} stayed normal.");
            // a game endpoint that is fine while references are not points away from a general upstream problem
            foreach (var e in endpoints.Where(e => e.RecentSampleCount >= MinSamples && IsStable(e)))
                f.Contradictions.Add($"Game-process endpoint {e.TargetName} stayed normal.");
            if (f.Contradictions.Count > 0) f.Confidence = Lower(f.Confidence);
            findings.Add(f);
        }
        else if (gatewayStable && degradedRefs.Count == 1 && refsWithData.Count >= 3)
        {
            IsDegraded(degradedRefs[0], out var why);
            findings.Add(new GameFinding
            {
                Id = "single-reference",
                Title = $"Only {degradedRefs[0].TargetName} looks degraded",
                Severity = FindingSeverity.Notice,
                Confidence = FindingConfidence.Low,
                Observations = { $"{degradedRefs[0].TargetName}: {why}.", "Your router and the other reference targets stayed normal." },
                PossibleExplanations = { "A problem specific to that provider or the path to it; it does not by itself indicate a problem with your connection." },
                Evidence = { $"{degradedRefs[0].TargetName}: {why}" },
                Contradictions = stableRefsText(refsWithData, degradedRefs),
                NextTest = "No action needed unless more targets degrade."
            });
        }
    }

    private static List<string> stableRefsText(List<TargetMetrics> all, List<TargetMetrics> degraded) =>
        all.Except(degraded).Select(r => $"{r.TargetName} stayed normal.").ToList();

    private static void AddEndpointFindings(List<GameFinding> findings, List<TargetMetrics> endpoints, bool gatewayStable,
        List<TargetMetrics> refsWithData, List<TargetMetrics> degradedRefs, GameDiagnosticInput input)
    {
        foreach (var e in endpoints)
        {
            string method = e.Protocol == NetworkProtocol.Tcp ? "TCP connect" : "ICMP";
            bool stillConnected = input.ActiveConnections.Any(c => c.IsActive && c.Remote != null && e.TargetHost == c.Remote.Address.ToString().Split('%')[0]);

            if (e.ReplyCount == 0 && e.TotalProbes >= 5)
            {
                var f = new GameFinding
                {
                    Id = "endpoint-silent:" + e.TargetId,
                    Title = $"{e.TargetName} has not answered {method} probes",
                    Severity = FindingSeverity.Notice,
                    Confidence = FindingConfidence.Low,
                    Observations = { $"None of {e.TotalProbes} {method} probes to {e.TargetHost} received a reply." },
                    PossibleExplanations =
                    {
                        "The endpoint (or a firewall in front of it) may simply filter these probes; many game and cloud servers do.",
                        "The measurement method may not suit this service."
                    },
                    NextTest = e.Protocol == NetworkProtocol.Icmp ? "Try the TCP method on this endpoint if it is a TCP connection, or rely on the reference measurements."
                                                                   : "Try ICMP, or rely on the reference measurements."
                };
                if (stillConnected) f.Contradictions.Add("The game process still has an active connection to this address, so it is not simply unreachable.");
                findings.Add(f);
                continue;
            }

            if (e.RecentSampleCount < MinSamples) continue;

            bool degraded = IsDegraded(e, out var why);
            bool everReplied = e.ReplyCount > 0;
            bool referencesStable = gatewayStable && refsWithData.Count >= 2 && degradedRefs.Count == 0;

            if (degraded && everReplied && referencesStable)
            {
                var f = new GameFinding
                {
                    Id = "endpoint-specific:" + e.TargetId,
                    Title = $"Possible game-specific connectivity issue ({e.TargetName})",
                    Severity = FindingSeverity.Warning,
                    Confidence = stillConnected ? FindingConfidence.Low : FindingConfidence.Medium,
                    Observations =
                    {
                        $"{e.TargetName}: {why}.",
                        "Your router and the reference targets stayed normal during the same window."
                    },
                    PossibleExplanations =
                    {
                        "A problem on the route to this particular endpoint or on the service itself.",
                        "The endpoint rate-limiting probes, which does not necessarily affect game traffic.",
                        "This is not proven to be your match server: its role is unknown."
                    },
                    Evidence = { $"{e.TargetName}: {why}", "Router: stable", $"{refsWithData.Count} reference targets: stable" },
                    NextTest = "Run an MTR/route trace to this endpoint and note whether the in-game lag coincides with it (Mark lag)."
                };
                if (stillConnected) f.Contradictions.Add("The game process still shows an active connection to this address.");
                findings.Add(f);
            }
            else if (degraded && degradedRefs.Count > 0)
            {
                findings.Add(new GameFinding
                {
                    Id = "endpoint-with-references:" + e.TargetId,
                    Title = $"{e.TargetName} degraded together with other measurements",
                    Severity = FindingSeverity.Notice,
                    Confidence = FindingConfidence.Medium,
                    Observations = { $"{e.TargetName}: {why}.", $"{degradedRefs.Count} reference target(s) degraded in the same window." },
                    PossibleExplanations = { "Consistent with a broader network problem rather than something specific to this endpoint." },
                    NextTest = "See the local-network / beyond-local finding for the next test."
                });
            }
            else if (!degraded && degradedRefs.Count >= 2)
            {
                findings.Add(new GameFinding
                {
                    Id = "contradiction:" + e.TargetId,
                    Title = "Measurements disagree: references degraded, game-process endpoint steady",
                    Severity = FindingSeverity.Notice,
                    Confidence = FindingConfidence.Low,
                    Observations = { $"{e.TargetName} was normal while {degradedRefs.Count} reference targets degraded." },
                    PossibleExplanations =
                    {
                        "The degradation may be specific to the paths to the reference targets and not to your game traffic.",
                        "Not enough information to blame the ISP or the local network."
                    },
                    NextTest = "Keep monitoring; mark lag moments to see which measurements actually move with them."
                });
            }
        }
    }

    private static void AddConnectionChangeFinding(List<GameFinding> findings, GameDiagnosticInput input, bool somethingDegraded)
    {
        // Web/API connections (HTTPS) open and close all the time - games reconnect to them every minute or two - so their
        // closing says nothing about the match. Only other public connections ending is worth a note.
        var ended = input.RecentChanges.Where(c => c.Kind == ConnectionChangeKind.Disappeared && c.Connection.HasRemote
            && c.Connection.Scope == AddressScope.Public && ConnectionKinds.Describe(c.Connection).Kind != ConnectionKind.WebService).ToList();
        if (ended.Count == 0) return;

        findings.Add(new GameFinding
        {
            Id = "connection-changes",
            Title = $"{ended.Count} game connection(s) ended recently",
            Severity = somethingDegraded ? FindingSeverity.Notice : FindingSeverity.Info,
            Confidence = FindingConfidence.Low,
            Observations = ended.Take(5).Select(c => c.Description).ToList(),
            PossibleExplanations =
            {
                "Normal: finishing a match, switching servers or services, or short-lived API calls.",
                somethingDegraded ? "It coincided with degraded measurements, which may or may not be related." : "No degraded measurements accompanied it."
            },
            NextTest = "If you were disconnected from a match, note the time with Mark lag and check the game's service status page."
        });
    }

    private static FindingConfidence Lower(FindingConfidence c) => c == FindingConfidence.High ? FindingConfidence.Medium : FindingConfidence.Low;
}
