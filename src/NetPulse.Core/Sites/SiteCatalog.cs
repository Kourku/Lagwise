using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPulse.Core.Sites;

/// <summary>A site the Site check looks at.</summary>
/// <param name="Host">The address a browser opens, e.g. "www.youtube.com".</param>
/// <param name="StatusApi">The site's official public status page in Statuspage format (<c>/api/v2/status.json</c>), when it has one.</param>
public sealed record SiteDefinition(string Id, string Name, string Host, string Category, string? StatusApi = null, bool BuiltIn = true)
{
    /// <summary>The first letters shown when the site has no icon yet.</summary>
    public string Monogram => Name.Length == 0 ? "?" : Name.StartsWith("X", StringComparison.Ordinal) && Name.Length > 1 && !char.IsLetter(Name[1]) ? "X"
        : string.Concat(Name.Split(' ', '-', '.').Where(w => w.Length > 0).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>
/// The popular sites checked by default. Status pages are listed only where the site publishes a public one that answers
/// without an account or key (each was checked against the real address); the others have none, which the page says.
/// </summary>
public static class SiteCatalog
{
    public static IReadOnlyList<SiteDefinition> Popular { get; } = new List<SiteDefinition>
    {
        new("youtube", "YouTube", "www.youtube.com", "Video"),
        new("x", "X (Twitter)", "x.com", "Social"),
        new("reddit", "Reddit", "www.reddit.com", "Social", "https://www.redditstatus.com/api/v2/status.json"),
        new("twitch", "Twitch", "www.twitch.tv", "Video", "https://status.twitch.com/api/v2/status.json"),
        new("discord", "Discord", "discord.com", "Chat", "https://discordstatus.com/api/v2/status.json"),
        new("google", "Google", "www.google.com", "Search"),
        new("wikipedia", "Wikipedia", "www.wikipedia.org", "Reference", "https://www.wikimediastatus.net/api/v2/status.json"),
        new("instagram", "Instagram", "www.instagram.com", "Social"),
        new("facebook", "Facebook", "www.facebook.com", "Social"),
        new("tiktok", "TikTok", "www.tiktok.com", "Video"),
        new("netflix", "Netflix", "www.netflix.com", "Video"),
        new("spotify", "Spotify", "open.spotify.com", "Music"),
        new("steam", "Steam", "store.steampowered.com", "Gaming"),
        new("epic", "Epic Games", "www.epicgames.com", "Gaming", "https://status.epicgames.com/api/v2/status.json"),
        new("riot", "Riot Games", "www.riotgames.com", "Gaming"),
    };

    /// <summary>
    /// A site added by hand: accepts "example.com", "https://example.com/page" or "Example.com:443" and keeps the host.
    /// Null when it is not a usable public host name.
    /// </summary>
    public static SiteDefinition? Custom(string input)
    {
        string? host = NormalizeHost(input);
        return host == null ? null : new SiteDefinition("custom:" + host, host, host, "Added by you", BuiltIn: false);
    }

    public static string? NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) return null;
        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        // a public name has a dot and a top-level part of letters; "localhost" or "router" are not sites on the internet
        int dot = host.LastIndexOf('.');
        if (dot <= 0 || dot == host.Length - 1 || host.Length > 253) return null;
        string tld = host[(dot + 1)..];
        if (!(tld.All(char.IsLetter) || tld.StartsWith("xn--", StringComparison.Ordinal))) return null;
        return host;
    }
}
