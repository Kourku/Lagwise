using System;
using System.Collections.Generic;
using System.Linq;
using NetPulse.Core;
using NetPulse.Core.Sessions;

namespace NetPulse.App.ViewModels;

/// <summary>One part of the connection in a verdict: home network, provider and internet, this PC.</summary>
public sealed record VerdictLayerItem(string Name, string Level, string Rating, string Text, string Glyph);

/// <summary>A moment the player marked as laggy, and what NetPulse saw then.</summary>
public sealed record VerdictLagItem(string TimeText, string Note, string Headline, string Detail, string Level);

/// <summary>
/// A recording's verdict ("was it my connection?") ready for the screen: used by the Game Monitor (the last session, and
/// the one being recorded so far) and by History for every recording.
/// </summary>
public sealed class VerdictViewModel
{
    public VerdictViewModel(SessionVerdict v, SessionStats? stats = null)
    {
        Verdict = v;
        Level = LevelOf(v.Level);
        Headline = v.Headline;
        Summary = v.Summary;
        Layers = v.Layers.Select(l => new VerdictLayerItem(l.Name, LevelOf(l.Level), SessionReportBuilder.Rating(l.Level), l.Text, GlyphOf(l.Name))).ToList();
        Episodes = v.Episodes.Take(12).Select(e => e.Text).ToList();
        MoreEpisodes = v.Episodes.Count > 12 ? $"...and {v.Episodes.Count - 12} more in the report." : "";
        LagMarks = v.LagMarks.Select((l, i) =>
        {
            string detail = l.Detail;
            if (stats != null && i < stats.LagCorrelations.Count && stats.LagCorrelations[i].Traffic is { } traffic && l.Cause == null)
                detail += " " + traffic.Describe();
            return new VerdictLagItem(l.T.ToLocalTime().ToString("HH:mm:ss"), string.IsNullOrEmpty(l.Note) ? "" : l.Note!,
                l.Headline, detail, l.ConnectionLookedFine ? (l.ProcessorBusy ? "Fair" : "Good") : l.Cause == null ? "Unknown" : "Poor");
        }).ToList();
        Advice = v.Advice.ToList();
        Numbers = string.Join("   ·   ", new[]
        {
            v.InternetTypicalMs is double ms ? $"Ping to the internet {ms:F0} ms" : null,
            v.RouterTypicalMs is double r ? $"router {r:F0} ms" : null,
            v.InternetLossPercent is double loss ? $"{loss:F1}% lost" : null,
            v.GameTime is TimeSpan g ? $"played {Duration(g)}" : v.Measured > TimeSpan.Zero ? $"measured {Duration(v.Measured)}" : null
        }.Where(x => x != null));
    }

    public SessionVerdict Verdict { get; }
    /// <summary>"Good", "Fair", "Poor" or "Unknown" (for a status mark).</summary>
    public string Level { get; }
    public string Headline { get; }
    public string Summary { get; }
    public string Numbers { get; }
    public IReadOnlyList<VerdictLayerItem> Layers { get; }
    public IReadOnlyList<string> Episodes { get; }
    public string MoreEpisodes { get; }
    public bool HasEpisodes => Episodes.Count > 0;
    public IReadOnlyList<VerdictLagItem> LagMarks { get; }
    public bool HasLagMarks => LagMarks.Count > 0;
    public IReadOnlyList<string> Advice { get; }
    public bool HasAdvice => Advice.Count > 0;

    public static string LevelOf(VerdictLevel l) => l switch
    {
        VerdictLevel.Good => "Good",
        VerdictLevel.Fair => "Fair",
        VerdictLevel.Poor => "Poor",
        _ => "Unknown"
    };

    private static string GlyphOf(string layer) => layer switch
    {
        "This PC" => "",                 // PC
        "Provider and internet" => "",   // globe
        _ => ""                          // Wi-Fi / home network
    };

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";
}
