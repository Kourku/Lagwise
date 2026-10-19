using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPulse.Core.Games;
using NetPulse.Core.Services;
using NetPulse.Core.Sites;

namespace NetPulse.Core.Security;

/// <summary>What the security check reads. Replaced by fakes in tests.</summary>
public interface ISecurityProbes
{
    FirewallState? Firewall();
    IReadOnlyList<Listener>? Listeners();
    Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct);
    Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct);
    Task<TlsObservation> TlsAsync(string host, IPAddress? address, CancellationToken ct);
    Task<bool?> UpnpAsync(CancellationToken ct);
}

/// <summary>
/// Runs the security check. The local part (firewall, network type, Wi-Fi, programs open to the network) reads only what
/// Windows reports. The network part, run when asked, looks up a made-up name (your DNS and public DNS), opens a secure
/// connection to three well-known sites and asks the router whether it offers UPnP; nothing else is sent.
/// </summary>
public sealed class SecurityCheckService
{
    /// <summary>Sites whose certificates are checked: large, stable, and run by different companies.</summary>
    public static readonly string[] TestSites = { "www.google.com", "www.cloudflare.com", "www.wikipedia.org" };

    private readonly ISecurityProbes _probes;
    private readonly Func<NetworkInterfaceInfoView> _nic;

    /// <summary>The Wi-Fi details the check needs from the active adapter.</summary>
    public sealed record NetworkInterfaceInfoView(bool IsWifi, string? Authentication, string? Cipher);

    public SecurityCheckService(Func<NetworkInterfaceInfoView> nic, ISecurityProbes? probes = null)
    {
        _nic = nic;
        _probes = probes ?? (OperatingSystem.IsWindows() ? new WindowsSecurityProbes() : new NoProbes());
    }

    public SecurityEvidence Local()
    {
        var nic = _nic();
        return new SecurityEvidence
        {
            Firewall = _probes.Firewall(),
            OnWifi = nic.IsWifi,
            WifiAuthentication = nic.Authentication,
            WifiCipher = nic.Cipher,
            Listeners = _probes.Listeners()
        };
    }

    public async Task<SecurityEvidence> FullAsync(CancellationToken ct)
    {
        var local = Local();
        // a name that cannot exist: a random label under a real top-level domain
        string madeUp = $"netpulse-check-{Guid.NewGuid():N}".Substring(0, 30) + ".com";
        var yoursTask = _probes.SystemDnsAsync(madeUp, ct);
        var publicTask = _probes.PublicDnsAsync(madeUp, ct);
        var upnpTask = _probes.UpnpAsync(ct);
        var tls = new List<TlsObservation>();
        foreach (var site in TestSites)
        {
            var dns = await _probes.SystemDnsAsync(site, ct);
            var address = dns.Outcome == DnsOutcome.Ok ? dns.Addresses.FirstOrDefault(a => !SiteDiagnosis.IsDeadEndAddress(a) && a.AddressFamily == AddressFamily.InterNetwork)
                ?? dns.Addresses.FirstOrDefault(a => !SiteDiagnosis.IsDeadEndAddress(a)) : null;
            tls.Add(await _probes.TlsAsync(site, address, ct));
        }
        return new SecurityEvidence
        {
            Firewall = local.Firewall,
            OnWifi = local.OnWifi,
            WifiAuthentication = local.WifiAuthentication,
            WifiCipher = local.WifiCipher,
            Listeners = local.Listeners,
            MadeUpNameYourDns = await yoursTask,
            MadeUpNamePublicDns = await publicTask,
            Tls = tls,
            Upnp = await upnpTask
        };
    }

    private sealed class NoProbes : ISecurityProbes
    {
        public FirewallState? Firewall() => null;
        public IReadOnlyList<Listener>? Listeners() => null;
        public Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct) => Task.FromResult(DnsAnswer.Failed("not available"));
        public Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct) => Task.FromResult(DnsAnswer.Failed("not available"));
        public Task<TlsObservation> TlsAsync(string host, IPAddress? address, CancellationToken ct) => Task.FromResult(new TlsObservation(host, false, false, null, false));
        public Task<bool?> UpnpAsync(CancellationToken ct) => Task.FromResult<bool?>(null);
    }
}

/// <summary>The real readings, through Windows' own interfaces; none needs administrator rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityProbes : ISecurityProbes
{
    private readonly SiteProbes _sites = new();

    /// <summary>Through the firewall's own API (HNetCfg.FwPolicy2), late bound so no interop library is shipped.</summary>
    public FirewallState? Firewall()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return null;
            object policy = Activator.CreateInstance(type)!;
            try
            {
                int active = Convert.ToInt32(type.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null));
                bool On(int p) => Convert.ToBoolean(type.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { p }));
                return new FirewallState(active, On(FirewallState.Domain), On(FirewallState.Private), On(FirewallState.Public));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(policy);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException or InvalidCastException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<Listener>? Listeners()
    {
        try
        {
            var snapshot = new WindowsConnectionSource().Capture();
            var names = new Dictionary<int, string>();
            string Name(int pid) => names.TryGetValue(pid, out var n) ? n : names[pid] = pid == 4 ? "System" : OnlinePrograms.ProcessName(pid) ?? $"process {pid}";
            return snapshot.Connections
                .Where(c => c.Protocol == TransportProtocol.Tcp && c.State == ConnectionState.Listen && !IPAddress.IsLoopback(c.LocalAddress))
                .Select(c => new Listener(Name(c.Pid), c.LocalPort, c.LocalAddress))
                .DistinctBy(l => (l.Program, l.Port))
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task<DnsAnswer> SystemDnsAsync(string host, CancellationToken ct) => _sites.SystemDnsAsync(host, ct);
    public Task<DnsAnswer> PublicDnsAsync(string host, CancellationToken ct) => _sites.PublicDnsAsync(host, ct);

    public async Task<TlsObservation> TlsAsync(string host, IPAddress? address, CancellationToken ct)
    {
        if (address == null) return new TlsObservation(host, false, false, null, false, "no address");
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(new IPEndPoint(address, 443), cts.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            SslPolicyErrors errors = SslPolicyErrors.None;
            X509Certificate2? root = null;
            using var ssl = new SslStream(stream, false, (_, _, chain, e) =>
            {
                errors = e;
                if (chain != null && chain.ChainElements.Count > 0) root = new X509Certificate2(chain.ChainElements[^1].Certificate);
                return true;   // judged below; nothing is sent over this connection
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, cts.Token);
            string? rootName = root?.GetNameInfo(X509NameType.SimpleName, false);
            return new TlsObservation(host, true, errors == SslPolicyErrors.None, rootName, root != null && IsPublicRoot(root),
                errors == SslPolicyErrors.None ? null : errors.ToString());
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or System.IO.IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new TlsObservation(host, false, false, null, false, ex.GetBaseException().Message);
        }
    }

    private static HashSet<string>? _authRoots;

    /// <summary>
    /// A root Windows distributes itself: in the "Third-Party Root Certification Authorities" store (filled from
    /// Microsoft's list of public authorities) or one of Microsoft's own. A root added by a program or a company sits only
    /// in "Trusted Root Certification Authorities".
    /// </summary>
    private static bool IsPublicRoot(X509Certificate2 root)
    {
        if (root.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && root.Issuer == root.Subject) return true;
        if (_authRoots == null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var store = new X509Store(StoreName.AuthRoot, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates) set.Add(c.Thumbprint);
            _authRoots = set;
        }
        return _authRoots.Contains(root.Thumbprint);
    }

    /// <summary>Asks the local network once for an Internet Gateway Device (SSDP multicast); true when one answers within 3 seconds.</summary>
    public async Task<bool?> UpnpAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            byte[] search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
            await udp.SendAsync(search, search.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var reply = await udp.ReceiveAsync(cts.Token);
            return Encoding.ASCII.GetString(reply.Buffer).Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) ? true : null;
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}
