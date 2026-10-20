# 🔬 NetPulse Measurement Methodology & Statistical Definitions

## 1. Measurement Validity & Multi-Protocol Philosophy

A fundamental flaw of basic ping utilities is the assumption that an ICMP echo response accurately represents real-world Internet connection health.

### Why ICMP Ping Alone is Insufficient:
1. **Control-Plane Deprioritization:** In modern carrier and enterprise routers, transit packets are forwarded entirely in dedicated hardware (ASICs/NPUs) on the *data plane*. Generating an ICMP Echo Reply requires handing the packet to the router's main CPU on the *control plane*. Many routers rate-limit or deprioritize ICMP generation to protect their CPU, resulting in synthetic packet loss or artificial latency spikes that do not affect user traffic.
2. **Protocol Discrimination:** Firewalls, ISPs, and carrier middleboxes frequently drop or rate-limit ICMP while allowing TCP and HTTPS traffic to pass unimpeded.
3. **Asymmetric Routing:** The path taken by packets from your PC to a destination often differs from the return path taken by response packets.

### The NetPulse Multi-Protocol Solution:
NetPulse measures connection quality across multiple independent layers:
- **Layer 3 (ICMP Echo):** Baseline reachability and hop discovery.
- **Layer 4 (TCP SYN/ACK Handshake):** Measures Layer 4 connection establishment to standard ports (80, 443).
- **Layer 7 (HTTPS Handshake & TTFB):** Isolates DNS lookup, TCP connect, TLS 1.3 cryptographic negotiation, and Time to First Byte (TTFB).
- **DNS Resolution (UDP Port 53):** Benchmarks system and public resolver query execution times using raw RFC 1035 wire packets.

---

## 2. Statistical Calculations

### 2.1 RFC 3550 Interarrival Jitter
NetPulse rejects the naive definition of jitter as $\text{Max Latency} - \text{Min Latency}$, which merely reflects outliers rather than latency variation.

Instead, NetPulse implements the standard statistical interarrival jitter defined in **RFC 3550 Section 6.4.1** (used globally in VoIP, RTP, and WebRTC streaming):

Given consecutive latency measurements $L_{i-1}$ and $L_i$:
$$D(i-1, i) = |L_i - L_{i-1}|$$

The smoothed jitter $J_i$ is updated recursively using an exponentially weighted moving average with smoothing factor $\alpha = \frac{1}{16}$:
$$J_i = J_{i-1} + \frac{D(i-1, i) - J_{i-1}}{16}$$

If $J_0$ is uninitialized, $J_0 = D(0, 1)$.

### 2.2 Rolling Median & Percentiles (P95)
Network latency distributions are typically skewed with long right tails. While rolling arithmetic means are displayed for reference, NetPulse prioritizes **Median (P50)** and **95th Percentile (P95)**:

For a sorted sample array $X$ of size $N$ and percentile $p \in [0, 1]$:
$$\text{Rank} = p \times (N - 1)$$
$$k = \lfloor \text{Rank} \rfloor, \quad d = \text{Rank} - k$$
$$P_p = X[k] + d \times (X[k+1] - X[k])$$

P95 represents the latency threshold below which 95% of all network probes fell, effectively identifying intermittent lag spikes without being distorted by a single timeout.

### 2.3 Packet Loss Percentage
$$\text{Loss } (\%) = \left( \frac{\text{Failed Probes}}{\text{Total Probes}} \right) \times 100$$

---

## 3. Hop-by-Hop Path & Route Diagnostics

### 3.1 Control-Plane vs Transit Packet Loss
When analyzing hop-by-hop traceroute / MTR output:
- **Case A (Control-Plane ICMP Rate-Limiting):**
  $$\text{Hop } 3: 45\text{ ms (80\% Loss)}, \quad \text{Hop } 4: 12\text{ ms (0\% Loss)}, \quad \text{Destination}: 14\text{ ms (0\% Loss)}$$
  *Interpretation:* Hop 3 is rate-limiting ICMP responses. Because subsequent hops and the destination exhibit 0% loss, transit traffic across Hop 3 is completely unaffected.
- **Case B (True Path Degradation):**
  $$\text{Hop } 3: 12\text{ ms (0\% Loss)}, \quad \text{Hop } 4: 85\text{ ms (15\% Loss)}, \quad \text{Destination}: 88\text{ ms (15\% Loss)}$$
  *Interpretation:* Packet loss and latency introduced at Hop 4 propagate through all downstream hops and directly affect the destination. NetPulse flags Hop 4 with `⚠️ Sustained Jump`.

### 3.2 Route Divergence Point
When comparing paths toward two destinations $A$ and $B$:
$$\text{Hops}_A = [h_{A,1}, h_{A,2}, \dots, h_{A,n}]$$
$$\text{Hops}_B = [h_{B,1}, h_{B,2}, \dots, h_{B,m}]$$

The divergence hop $k$ is the smallest index where:
$$\text{IP}(h_{A,k}) \neq \text{IP}(h_{B,k})$$

Common hops $[1 \dots k-1]$ represent shared local and ISP aggregation paths, while hops beyond $k$ indicate where routing diverges to transit providers or destination networks.

---

## 4. Bufferbloat & Loaded Latency Methodology

### 4.1 Test Phases
1. **Unloaded Baseline:** Probe the target 8 times in an idle state. Calculate baseline median latency $L_{\text{base}}$.
2. **Download Saturation:** 10 seconds of 6 parallel HTTP GET streams from the test provider, pinging concurrently. Pings from the first 2.5 seconds, while the transfer speeds up, are shown but not graded. Calculate loaded download latency $L_{\text{dl}}$ and delta $\Delta_{\text{dl}} = \max(0, L_{\text{dl}} - L_{\text{base}})$. A phase needs at least 2 MB to count; the speed reached is stored with the result.
3. **Upload Saturation:** 10 seconds of 4 parallel HTTP POST streams with random 256 KB payloads, pinging concurrently, with the same warm-up rule. Calculate loaded upload latency $L_{\text{ul}}$ and delta $\Delta_{\text{ul}} = \max(0, L_{\text{ul}} - L_{\text{base}})$.

### 4.2 Grading Scale
$$\Delta_{\max} = \max(\Delta_{\text{dl}}, \Delta_{\text{ul}})$$

- **A+ ($\le 5\text{ ms}$):** Ideal. Perfect queue discipline.
- **A ($5 - 15\text{ ms}$):** Excellent. Minimal buffering.
- **B ($15 - 30\text{ ms}$):** Good. Minor transient queuing.
- **C ($30 - 60\text{ ms}$):** Noticeable. Interactive gaming/video calls degrade under load.
- **D ($60 - 150\text{ ms}$):** Poor. Heavy buffer bloat.
- **F ($> 150\text{ ms}$):** Critical. Router buffers hold hundreds of milliseconds of data.

### 4.3 Observed in Real Use (since 0.14)
When this PC itself moves more than the heavy-traffic limit (15 Mbps by default) for at least 10 seconds, NetPulse compares the plain-ping reference servers during that stretch with the minute before it:

- **Before:** each reference server's median over the 60 s before the stretch started (not reaching back into an earlier stretch), taken when the stretch is first reported, because a long stretch outlasts the probe history. A server needs 5 replies.
- **During:** each server's median from 3 s after the start to the last heavy second. A server needs 5 replies.
- **Rise:** the median over servers of $L_{\text{during}} - L_{\text{before}}$, so every server is compared with itself and a far-away server never looks like a rise. Probes without a reply during the stretch are reported as a percentage.
- **Stretch boundaries:** dips below the limit shorter than 5 s do not end a stretch (downloads are bursty). NetPulse's own Speed and Bufferbloat tests are never counted, and a stretch they interrupt is dropped. Only the adapter Windows uses for the internet counts.

The rise is described (steady up to 15 ms, a little up to 30 ms, bufferbloat above), stored with the test results and shown on the Bufferbloat page, but **never graded**: the line may not have been full, so a steady ping proves little. In the "Ready to play?" checklist, a clear rise (above 30 ms) seen in the last 14 days counts as evidence of bufferbloat when no test was run; a steady ping does not count as a pass.

---

## 5. Drops and the Connection Diary (since 0.15)

### 5.1 When the connection counts as lost
A measurement cycle probes every target once. The internet counts as **down** after 3 cycles in a row in which no internet target answered: the reference servers and the HTTPS check count, the router and the DNS lookup do not (a router keeps answering when the modem or the provider is down, and it can answer DNS lookups from its cache). Internet servers going silent only counts once one of them has answered since start, so a network that blocks pings is not "down"; nothing answering at all, the router included, always counts. The drop is timed from the first failed cycle and records whether the router answered meanwhile: **beyond your router** (modem or provider) or **the router silent too** (cable, Wi-Fi or the router).

### 5.2 The diary
For every hour (UTC, shown in local time) NetPulse keeps how long it measured (pauses longer than 5 s or three intervals, such as sleep, are not counted) and, per plain-ping reference server: probes, replies, the median ping and the variation (mean change between consecutive replies). Probes during a drop are left out of probe loss: the drop already says it. A restart within an hour adds to the same hour (counts add up; medians are averaged by replies). Drops are kept separately; one still open when NetPulse closes ends at the last moment NetPulse measured and is marked as a minimum.

For display, each server's **usual** is the median of its hourly medians (at least 30 replies per hour, at least 3 such hours). **Ping above usual** for an hour is the reply-weighted mean of (hour median - usual) over servers, so a far-away server never looks slow. **Probe loss** only counts servers that answered at all in that hour (a server that never answers is filtering pings). An hour needs 5 minutes of measuring to be rated; a drop is always shown. Ratings follow the checklist's rules of thumb: a drop, loss of 3% or more, variation over 20 ms or ping 50 ms above usual is red; loss of 1% or more, variation over 8 ms or ping 15 ms above usual is amber.

## 6. Site Check (since 0.15)

Each site is opened the way a browser opens it, and the first step that fails tells where the problem is:

1. **This PC:** the hosts file. A line sending the site to an address that leads nowhere (0.0.0.0, 127.x, private ranges) is a block on this PC.
2. **Your DNS:** Windows' own lookup (the provider's or the router's DNS). No answer, "does not exist", or only dead-end addresses are suspicious.
3. **Public DNS:** the same name over DNS over HTTPS, from Cloudflare (`https://1.1.1.1/dns-query`) and, if that fails, Google (`https://8.8.8.8/resolve`), reached by address so a broken DNS cannot stop them. No account or key.
4. **Connect:** TCP to port 443 of the address your DNS gave (the public one when yours gave none).
5. **Secure handshake:** TLS naming the site (SNI). A certificate that is not valid for the site means something else answered (interception). A reset or timeout is repeated with a neutral name (`example.com`): if the same server answers that, connections are filtered by name.
6. **Answer:** one `GET /` over HTTP/1.1 (no cookies). Any answer proves the site reachable, including a bot check (never solved) or a refusal of automated visits; 451 is a legal block, 5xx a problem at the site; more than 3 s is slow.
7. **Status page:** for sites that publish one in the public Statuspage format (Reddit, Twitch, Discord, Wikipedia, Epic Games; each checked against the real address), its indicator and description.

When the address from your DNS fails but the address from public DNS works, your DNS sends you to the wrong server (often a block page). When no site in a check answers, the connection itself is down. From one PC, a site that is down and a site whose address is blocked look the same: the verdict says so, and the status page, where public, tells them apart.

## 7. Security Check (since 0.16)

Read from Windows, without administrator rights, as soon as the page opens:

- **Windows Firewall:** the firewall's own API (`HNetCfg.FwPolicy2`): which network types are active and whether the firewall is on for each. Off for the active type is a problem; off for another type is worth a look.
- **Network type:** Public (other devices cannot find this PC) is the safe default; Private is fine at home and a problem on an open Wi-Fi.
- **Wi-Fi protection:** `netsh wlan show interfaces` (by full path): open and WEP/WPA/TKIP are problems, WPA2 (AES) and WPA3 are fine.
- **Programs open to the network:** TCP listening sockets not limited to this PC, with their programs. Windows' own services are left out; remote-access ports (RDP 3389, VNC, Telnet, FTP, SSH, WinRM, TeamViewer) are worth a look, and a problem with the firewall off.

When **Check** is pressed:

- **Honest name lookups:** a made-up `.com` name is looked up with your DNS and with Cloudflare's public DNS. Your DNS answering with an address while public DNS says the name does not exist is NXDOMAIN redirection.
- **Real servers for well-known sites:** www.google.com, www.cloudflare.com and www.wikipedia.org are resolved with your DNS and a TLS handshake is made to that address (nothing is sent over it). A certificate that Windows does not accept means the address is not the site's, or the connection is intercepted.
- **Secure connections:** for the valid handshakes, the root of the certificate chain must be one Windows distributes itself (the "Third-Party Root Certification Authorities" store, filled from Microsoft's root program, or Microsoft's own roots). A root found only among "Trusted Root Certification Authorities" was added on this PC: antivirus HTTPS scanning, a company proxy, or something unwanted.
- **UPnP:** one SSDP search for an Internet Gateway Device on the local network (3 seconds). An answer means programs can open ports on the router; no answer is reported as unknown, because a Public network's firewall hides the reply.

Information lines (UPnP on, a Private network at home, programs listening) and unknown ones never make the verdict worse.

## 8. Diagnostic Evidence Attribution Matrix

| Gateway Health | Internet Targets | DNS Resolver | Bufferbloat Delta | Diagnostic Classification |
|---|---|---|---|---|
| Latency $> 25\text{ms}$ or Loss $> 4\%$ | Degraded | Any | Any | **Local Network / Router / Wi-Fi Issue** |
| Low ($\le 5\text{ms}$), 0% Loss | High Latency / Loss on $\ge 2$ targets | Normal | Normal | **ISP / Upstream Internet Issue** |
| Low ($\le 5\text{ms}$), 0% Loss | Normal | Failing or $> 180\text{ms}$ | Normal | **DNS Resolver Issue** |
| Low ($\le 5\text{ms}$), 0% Loss | 1 Target Failing, Others Normal | Normal | Normal | **Remote Target Specific Issue** |
| Low ($\le 5\text{ms}$), 0% Loss | Normal Unloaded | Normal | Delta $> 60\text{ms}$ | **Bufferbloat / Queue Congestion** |
| Low ($\le 5\text{ms}$), 0% Loss | Normal ($\le 50\text{ms}$), 0% Loss | Normal | Delta $\le 15\text{ms}$ | **Optimal Connection** |
| Interface Down or 100% Loss | 100% Loss | 100% Loss | N/A | **Disconnected** |
| Minor fluctuations | Mild variation | Normal | Normal | **Inconclusive / Mild Variability** |

## 9. Session Verdict (since 1.0)

Every recording (a game session or a network recording) is judged after the fact from its own measurements: "was it my connection, and which part?" The same rules run live on the session so far, and History works them out again for older recordings.

**Inputs.** The router (ICMP), the reference servers measured with plain pings (or TCP connects; an HTTPS handshake or a DNS lookup includes server work and is used only when no plain ping exists), connection drops and monitoring gaps as recorded, and each second of this PC's traffic, processor load (Windows' `GetSystemTimes`, as Task Manager shows it) and Wi-Fi signal.

**Norms.** Each series is compared with its own median in the recording, never with a fixed ping. A sample is *slow* above $\max(3\,n,\; n + 30\text{ ms})$ for the router and $n + \max(50\text{ ms},\; 0.5\,n)$ for a server, where $n$ is that series' median; a lost probe is slow too.

**Slots.** The recording is cut into 5-second slots. A series is unsteady in a slot when at least 40% of its probes there (at least one) were slow. A series unsteady in more than 30% of the slots it was measured in (12 or more) is noisy by nature, typically a public resolver rate-limiting pings, and takes no part. Slots inside a monitoring gap are neither trouble nor measured.

**Cause of a slot**, in order:
1. inside a recorded drop: **Outage**;
2. the router unsteady: **Home network** (Wi-Fi or router);
3. a strict majority of the remaining servers unsteady (1 of 1, 2 of 2, 2 of 3, 3 of 4): **Provider** (beyond the router), or **This PC's traffic** when the PC moved more than the heavy-traffic limit (Settings, 15 Mbps by default) for at least 2 seconds of the slot.

Bad slots of one cause with at most one calm slot between them form one stretch.

**Rating.**
- *Not enough data* below 30 s of judged slots.
- *Poor* with a drop of 10 s or more, trouble in at least 5% of the slots, or 12 or more stretches per hour.
- *Some trouble* with any shorter drop, 2 or more stretches per hour, or a stretch of 15 s or more.
- *Good* otherwise: one short spike in an hour is normal.

The headline names the cause with the most time (a drop of 10 s or more always leads).

**Layers.** Home network, Provider and internet, and This PC are rated separately: typical router and server ping, loss, how often and how long each part was slow, heavy traffic and processor time at 90% or more.

**Lag marks** look at $[t - 20\text{ s},\; t + 5\text{ s}]$ around the key press, since the key is pressed after the stutter. The cause there (outage > home network > own traffic > provider) is reported with the worst numbers against the usual ones. With no cause:
- a processor at 90% or more for at least a quarter of those seconds means "this PC was very busy";
- a game with no network connections at all means "the game was not online";
- otherwise "your connection looked normal": the lag was further away, at the game's server or on the route to it, which NetPulse cannot measure.
