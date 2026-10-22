using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Lagwise.Core.Security;

public enum SecurityStatus { Good, Fair, Poor, Info, Unknown }

/// <summary>One line of the security check: what was found, what it means, and what to do.</summary>
public sealed record SecurityItem(string Id, string Title, SecurityStatus Status, string Detail, string Advice = "");

public sealed record SecurityReport(SecurityStatus Overall, string Headline, IReadOnlyList<SecurityItem> Items, DateTime CheckedUtc);

/// <summary>Windows Firewall as the firewall API reports it (no administrator rights needed).</summary>
/// <param name="ActiveProfiles">Bit mask of the network types in use: 1 domain, 2 private, 4 public.</param>
public sealed record FirewallState(int ActiveProfiles, bool DomainOn, bool PrivateOn, bool PublicOn)
{
    public const int Domain = 1, Private = 2, Public = 4;
    public bool IsOn(int profile) => profile switch { Domain => DomainOn, Private => PrivateOn, _ => PublicOn };
    public IEnumerable<int> Active => new[] { Domain, Private, Public }.Where(p => (ActiveProfiles & p) != 0);
}

/// <summary>A program on this PC waiting for connections from other devices (a TCP listening socket not limited to this PC).</summary>
public sealed record Listener(string Program, int Port, IPAddress Address);

/// <summary>One secure connection to a well-known site, as Windows judged it.</summary>
/// <param name="Valid">The certificate is valid for the site and chains to a root Windows trusts.</param>
/// <param name="RootName">The root certificate the chain ends in.</param>
/// <param name="RootIsPublic">The root is one Windows distributes itself (Microsoft's list of public authorities), not one added on this PC.</param>
public sealed record TlsObservation(string Host, bool Connected, bool Valid, string? RootName, bool RootIsPublic, string? Error = null);

/// <summary>Everything the security check looked at. The network parts are null until a check with the network is run.</summary>
public sealed class SecurityEvidence
{
    public FirewallState? Firewall { get; init; }
    public bool OnWifi { get; init; }
    public string? WifiAuthentication { get; init; }
    public string? WifiCipher { get; init; }
    public IReadOnlyList<Listener>? Listeners { get; init; }

    /// <summary>A made-up name: did your DNS answer for it (it should not), and did public DNS say it does not exist?</summary>
    public Sites.DnsAnswer? MadeUpNameYourDns { get; init; }
    public Sites.DnsAnswer? MadeUpNamePublicDns { get; init; }
    /// <summary>Secure connections to well-known sites at the addresses your DNS gave.</summary>
    public IReadOnlyList<TlsObservation>? Tls { get; init; }
    /// <summary>True: the router answered a UPnP search; null: no answer.</summary>
    public bool? Upnp { get; init; }
    public bool NetworkChecked => Tls != null;
}

/// <summary>
/// "Is my connection safe?": a short checklist built from what Windows reports without administrator rights and from
/// a few harmless connections. Each line says what was found, what it means and what to do. Information lines
/// (UPnP, a Private network at home) and unknown ones never make the verdict worse.
/// </summary>
public static class SecurityCheck
{
    /// <summary>Ports that let someone control or read this PC from elsewhere: worth knowing when they are open.</summary>
    public static readonly IReadOnlyDictionary<int, string> RemoteAccessPorts = new Dictionary<int, string>
    {
        [21] = "FTP file sharing", [22] = "SSH remote shell", [23] = "Telnet remote shell", [3389] = "Remote Desktop",
        [5900] = "VNC remote control", [5901] = "VNC remote control", [5938] = "TeamViewer", [5985] = "Windows remote management", [5986] = "Windows remote management"
    };

    /// <summary>Windows itself listens on these for file sharing and system services; the firewall decides who reaches them.</summary>
    private static readonly HashSet<string> WindowsPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "svchost", "lsass", "wininit", "services", "spoolsv", "Idle"
    };

    public static SecurityReport Evaluate(SecurityEvidence e, DateTime checkedUtc)
    {
        var items = new List<SecurityItem>
        {
            Firewall(e.Firewall),
            NetworkType(e.Firewall, e.OnWifi, e.WifiAuthentication)
        };
        if (e.OnWifi) items.Add(Wifi(e.WifiAuthentication, e.WifiCipher));
        items.Add(OpenToNetwork(e.Listeners, e.Firewall));
        items.Add(MadeUpNames(e));
        items.Add(DnsAnswers(e.Tls));
        items.Add(Inspection(e.Tls));
        items.Add(Upnp(e));

        var overall = items.Any(i => i.Status == SecurityStatus.Poor) ? SecurityStatus.Poor
            : items.Any(i => i.Status == SecurityStatus.Fair) ? SecurityStatus.Fair
            : items.Any(i => i.Status == SecurityStatus.Good) ? SecurityStatus.Good : SecurityStatus.Unknown;
        string headline = overall switch
        {
            SecurityStatus.Good => e.NetworkChecked ? "Nothing on this PC or its connection looks unsafe." : "This PC looks safe. Press Check to test your connection too.",
            SecurityStatus.Fair => "Mostly fine, with something worth a look.",
            SecurityStatus.Poor => "Fix the item marked in red first.",
            _ => "Checking..."
        };
        return new SecurityReport(overall, headline, items, checkedUtc);
    }

    private static string ProfileName(int p) => p switch { FirewallState.Domain => "work (domain)", FirewallState.Private => "Private", _ => "Public" };

    private static SecurityItem Firewall(FirewallState? f)
    {
        const string title = "Windows Firewall";
        if (f == null) return new("firewall", title, SecurityStatus.Unknown, "Windows did not say whether its firewall is on.");
        var offActive = f.Active.Where(p => !f.IsOn(p)).ToList();
        var offOther = new[] { FirewallState.Domain, FirewallState.Private, FirewallState.Public }.Where(p => (f.ActiveProfiles & p) == 0 && !f.IsOn(p)).ToList();
        if (offActive.Count > 0)
            return new("firewall", title, SecurityStatus.Poor,
                $"Off for the network you are on ({string.Join(", ", offActive.Select(ProfileName))}). Other devices on it can reach every program on this PC that waits for connections.",
                "Turn it on in Windows Security > Firewall & network protection, unless another security program provides a firewall.");
        if (offOther.Count > 0)
            return new("firewall", title, SecurityStatus.Fair,
                $"On for this network, but off for {string.Join(" and ", offOther.Select(ProfileName))} networks: it would be off the next time you join one.",
                "Turn it on for every network type in Windows Security > Firewall & network protection.");
        return new("firewall", title, SecurityStatus.Good, "On for every kind of network.");
    }

    private static SecurityItem NetworkType(FirewallState? f, bool wifi, string? auth)
    {
        const string title = "Network type";
        if (f == null || f.Active.Count() == 0) return new("profile", title, SecurityStatus.Unknown, "Windows did not say whether this network is Public or Private.");
        if ((f.ActiveProfiles & FirewallState.Domain) != 0)
            return new("profile", title, SecurityStatus.Info, "A work network (domain), managed by your organisation.");
        if ((f.ActiveProfiles & FirewallState.Private) != 0)
        {
            bool open = wifi && WifiKind(auth) == WifiProtection.Open;
            return new("profile", title, open ? SecurityStatus.Poor : SecurityStatus.Info,
                open ? "Private, on an open Wi-Fi: this PC lets every device around it find it and its shared files."
                     : "Private: other devices on this network can find this PC and its shared files and printers. Fine at home.",
                open ? "Set this network to Public: Settings > Network & internet > Wi-Fi > this network > Network profile type."
                     : "On networks you do not control (cafés, hotels, school) choose Public instead. Games and voice chat work the same.");
        }
        return new("profile", title, SecurityStatus.Good, "Public: other devices on this network cannot find this PC or its shared files. The safest choice; games and voice chat work the same.");
    }

    public enum WifiProtection { Unknown, Open, Weak, Wpa2, Wpa3 }

    /// <summary>Reads Windows' description of the Wi-Fi protection ("WPA2-Personal", "WPA3-Personal", "Open", "WEP").</summary>
    public static WifiProtection WifiKind(string? authentication, string? cipher = null)
    {
        if (string.IsNullOrWhiteSpace(authentication)) return WifiProtection.Unknown;
        string a = authentication.ToUpperInvariant();
        if (a.Contains("WPA3", StringComparison.Ordinal) || a.Contains("SAE", StringComparison.Ordinal) || a.Contains("OWE", StringComparison.Ordinal)) return WifiProtection.Wpa3;
        if (a.Contains("WPA2", StringComparison.Ordinal))
            return cipher != null && cipher.Contains("TKIP", StringComparison.OrdinalIgnoreCase) ? WifiProtection.Weak : WifiProtection.Wpa2;
        if (a.Contains("WEP", StringComparison.Ordinal) || a.Contains("WPA", StringComparison.Ordinal) || a.Contains("SHARED", StringComparison.Ordinal)) return WifiProtection.Weak;
        if (a.Contains("OPEN", StringComparison.Ordinal) || a.Contains("OUVR", StringComparison.Ordinal) || a.Contains("OUVERT", StringComparison.Ordinal) || a.Contains("OFFEN", StringComparison.Ordinal) || a.Contains("ABIERT", StringComparison.Ordinal)) return WifiProtection.Open;
        return WifiProtection.Unknown;
    }

    private static SecurityItem Wifi(string? auth, string? cipher)
    {
        const string title = "Wi-Fi protection";
        string what = auth == null ? "" : $" ({auth}{(string.IsNullOrEmpty(cipher) ? "" : ", " + cipher)})";
        return WifiKind(auth, cipher) switch
        {
            WifiProtection.Open => new("wifi", title, SecurityStatus.Poor, $"This Wi-Fi is open{what}: what is not otherwise encrypted can be read by anyone nearby.",
                "Prefer a protected network, or a mobile hotspot. Sites with https stay encrypted, but names you look up and other traffic do not."),
            WifiProtection.Weak => new("wifi", title, SecurityStatus.Poor, $"This Wi-Fi uses old protection{what} that can be broken in minutes.",
                "Switch the router to WPA2 (AES) or WPA3 in its settings."),
            WifiProtection.Wpa3 => new("wifi", title, SecurityStatus.Good, $"Protected with WPA3{what}, the newest kind."),
            WifiProtection.Wpa2 => new("wifi", title, SecurityStatus.Good, $"Protected with WPA2{what}. WPA3, where the router offers it, is newer still."),
            _ => new("wifi", title, SecurityStatus.Unknown, "Windows did not say how this Wi-Fi is protected.")
        };
    }

    private static SecurityItem OpenToNetwork(IReadOnlyList<Listener>? listeners, FirewallState? f)
    {
        const string title = "Programs open to the network";
        if (listeners == null) return new("listening", title, SecurityStatus.Unknown, "The list of programs waiting for connections could not be read.");
        var remote = listeners.Where(l => RemoteAccessPorts.ContainsKey(l.Port)).GroupBy(l => l.Port).ToList();
        var apps = listeners.Where(l => !WindowsPrograms.Contains(l.Program) && !RemoteAccessPorts.ContainsKey(l.Port))
            .GroupBy(l => l.Program, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(l => l.Port).Distinct().OrderBy(p => p).Take(3))})").ToList();
        bool firewallOff = f != null && f.Active.Any(p => !f.IsOn(p));

        string list = apps.Count == 0 ? "Only Windows' own services wait for connections from other devices." :
            $"{apps.Count} program{(apps.Count == 1 ? "" : "s")} wait{(apps.Count == 1 ? "s" : "")} for connections from other devices: {string.Join(", ", apps.Take(6))}{(apps.Count > 6 ? $" and {apps.Count - 6} more" : "")}.";
        if (remote.Count > 0)
        {
            string names = string.Join(", ", remote.Select(g => $"{RemoteAccessPorts[g.Key]} (port {g.Key}, {g.First().Program})"));
            return new("listening", title, firewallOff ? SecurityStatus.Poor : SecurityStatus.Fair,
                $"Remote access is switched on: {names}. {list}",
                "If you did not set this up yourself, turn it off (for Remote Desktop: Settings > System > Remote Desktop) and check what installed it.");
        }
        if (firewallOff && apps.Count > 0)
            return new("listening", title, SecurityStatus.Poor, list + " With the firewall off, any device on this network can reach them.", "Turn Windows Firewall on.");
        return new("listening", title, apps.Count == 0 ? SecurityStatus.Good : SecurityStatus.Info, list + (apps.Count == 0 ? "" : " Usually normal (game launchers, music apps, sync); the firewall decides who may reach them."));
    }

    private static SecurityItem MadeUpNames(SecurityEvidence e)
    {
        const string title = "Honest name lookups";
        if (e.MadeUpNameYourDns == null) return new("nxdomain", title, SecurityStatus.Unknown, "Press Check to test: Lagwise looks up a made-up name, which should not exist.");
        var yours = e.MadeUpNameYourDns;
        if (yours.Outcome == Sites.DnsOutcome.NotFound)
            return new("nxdomain", title, SecurityStatus.Good, "Your DNS says a made-up name does not exist, as it should.");
        if (yours.Outcome == Sites.DnsOutcome.Ok && yours.Addresses.Count > 0 && e.MadeUpNamePublicDns?.Outcome != Sites.DnsOutcome.Ok)
            return new("nxdomain", title, SecurityStatus.Fair,
                $"Your DNS gives an address ({yours.Addresses[0]}) for a name that does not exist. Mistyped addresses then land on a page of its choosing, often advertising, and real problems are hidden.",
                "A public DNS (1.1.1.1, 8.8.8.8 or 9.9.9.9) in Windows or on your router answers honestly.");
        return new("nxdomain", title, SecurityStatus.Unknown, $"Your DNS did not answer clearly ({yours.Error ?? yours.Outcome.ToString()}).");
    }

    private static SecurityItem DnsAnswers(IReadOnlyList<TlsObservation>? tls)
    {
        const string title = "Real servers for well-known sites";
        if (tls == null) return new("dnsreal", title, SecurityStatus.Unknown, "Press Check to test: Lagwise opens secure connections to three well-known sites at the addresses your DNS gives.");
        var connected = tls.Where(t => t.Connected).ToList();
        if (connected.Count == 0) return new("dnsreal", title, SecurityStatus.Unknown, "None of the test sites could be reached.");
        var bad = connected.Where(t => !t.Valid).ToList();
        if (bad.Count > 0)
            return new("dnsreal", title, SecurityStatus.Poor,
                $"At the address your DNS gives, {string.Join(", ", bad.Select(b => b.Host))} answered with a certificate that is not theirs{(bad[0].Error != null ? $" ({bad[0].Error})" : "")}. Your connection is sent to another server or intercepted.",
                "Check your security software for web filtering; at home, check the router's DNS settings (a changed DNS is a common router attack); on public Wi-Fi, finish signing in.");
        return new("dnsreal", title, SecurityStatus.Good, $"Your DNS sends you to the real servers: {string.Join(", ", connected.Select(t => t.Host))} proved who they are with valid certificates.");
    }

    private static SecurityItem Inspection(IReadOnlyList<TlsObservation>? tls)
    {
        const string title = "Secure connections (HTTPS)";
        if (tls == null) return new("inspection", title, SecurityStatus.Unknown, "Press Check to test whether a program or network opens your secure connections.");
        var valid = tls.Where(t => t.Connected && t.Valid).ToList();
        if (valid.Count == 0) return new("inspection", title, SecurityStatus.Unknown, "No secure connection could be completed to judge this.");
        var local = valid.Where(t => !t.RootIsPublic).ToList();
        if (local.Count > 0)
            return new("inspection", title, SecurityStatus.Fair,
                $"Your secure connections are opened and checked by \"{local[0].RootName}\", a certificate authority installed on this PC rather than a public one. Antivirus programs and company networks do this on purpose.",
                "If you do not recognise it, find out what installed it (certmgr.msc > Trusted Root Certification Authorities) and remove that program.");
        return new("inspection", title, SecurityStatus.Good, "Your secure connections reach the sites directly: they end in public certificate authorities, and nothing on this PC or network opens them.");
    }

    private static SecurityItem Upnp(SecurityEvidence e)
    {
        const string title = "UPnP on your router";
        if (!e.NetworkChecked) return new("upnp", title, SecurityStatus.Unknown, "Press Check to ask your router whether programs can open ports on it automatically.");
        return e.Upnp == true
            ? new("upnp", title, SecurityStatus.Info, "On: programs on your network can open ports on the router without asking. Games and voice chat often rely on it; malware can use it too.",
                "Keep it if your games need it, and keep the router's firmware up to date.")
            : new("upnp", title, SecurityStatus.Unknown, "No answer: UPnP is off, or the firewall hides the router's reply on a Public network.");
    }
}
