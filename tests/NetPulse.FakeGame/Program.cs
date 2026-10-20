using System.Net;
using System.Net.Sockets;

// Behaves like an online game as far as Windows can tell: a UDP socket (where games send the match itself) and a TCP
// connection to an internet server (like a game's login or chat service), held open until it is closed or the time is up.
// Usage: StandInGame.exe [seconds=120]
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? Math.Clamp(s, 1, 3600) : 120;

using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));   // loopback: no firewall prompt
using var tcp = new TcpClient();
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await tcp.ConnectAsync("1.1.1.1", 443, cts.Token);                   // Cloudflare, also one of NetPulse's reference servers
}
catch (Exception ex) when (ex is SocketException or OperationCanceledException)
{
    // offline: the UDP socket alone still makes it an online-looking game
}

await Task.Delay(TimeSpan.FromSeconds(seconds));
