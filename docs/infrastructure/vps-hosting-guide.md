# 🛡️ Self-Hosting Behind a VPS Gateway

<div align="center">

[![Tailscale](https://img.shields.io/badge/Tailscale-VPN%20Mesh-000000?style=for-the-badge&logo=tailscale&logoColor=white)](https://tailscale.com/)
[![WireGuard](https://img.shields.io/badge/WireGuard-Kernel%20Tunnel-88171A?style=for-the-badge&logo=wireguard&logoColor=white)](https://www.wireguard.com/)
[![Cloudflare](https://img.shields.io/badge/Cloudflare-Edge%20WAF-F38020?style=for-the-badge&logo=cloudflare&logoColor=white)](https://www.cloudflare.com/)
[![COMPEL](https://img.shields.io/badge/COMPEL-1.0ms%20Pacer-107C41?style=for-the-badge)](#)

**The Enterprise-Grade Hybrid Hosting Model: Local Compute Sovereignty Shielded by a Cloud-Hardened Edge.**

</div>

---

## 🧭 1. Architectural Philosophy: The Hybrid Gateway

Hosting your own Project Kongor V master servers and COMPEL dedicated game instances on local hardware provides unparalleled control, multi-core computational horsepower, and zero recurring cloud compute bills. However, exposing a residential Internet connection directly to the public web introduces severe privacy vulnerabilities, security risks, and ISP routing limitations.

The **VPS Gateway Architecture** resolves this dilemma by deploying a lightweight, inexpensive cloud gateway in front of your local physical server:

```
[ Players / Game Clients / Web Browsers ]
                   │
                   ▼
  ┌─────────────────────────────────┐
  │   Cloudflare Edge Network       │  <-- DDoS mitigation, WAF, CDN caching,
  │   (DNS Proxy & SSL Termination) │      global Anycast edge, IP cloaking
  └────────────────┬────────────────┘
                   │ HTTPS / Web Traffic
                   ▼
  ┌─────────────────────────────────┐
  │   Sacrificial VPS Gateway       │  <-- Inexpensive ($3-$6/mo) cloud VPS
  │   (Traefik / Nginx / iptables)  │      Public endpoint with static data center IP
  └────────────────┬────────────────┘
                   │
                   │  Encrypted WireGuard / Tailscale Tunnel
                   │  (ZERO inbound ports opened on home router!)
                   │
                   ▼
  ┌─────────────────────────────────┐
  │   Local Physical Workstation    │  <-- High-performance bare metal hardware
  │   (Project Kongor V Core)       │      Runs Master Server, COMPEL game slaves,
  │                                 │      PostgreSQL/Redis, and replay engines
  └─────────────────────────────────┘
```

---

## 🔒 2. Key Security & Operational Benefits

### 🛡️ Residential IP Shielding & Total Anonymity
When hosting directly on a home connection, every HTTP request, chat packet, and UDP game connection exposes your residential public IP address. Residential IPs can be easily geolocated to your city or neighborhood and linked to your personal ISP subscription.
- **Zero Residential Exposure**: External players and scanners only ever see the IP of the cloud VPS or Cloudflare's Anycast edge.
- **Protection Against Targeted Harassment & Doxxing**: Keeps developer and server administrator home locations private.

### 🧱 Sacrificial Boundary & Perimeter Defense
The VPS acts as an isolated, disposable security buffer:
- **No Inbound Router Ports**: By using outbound-initiated mesh VPN tunnels (such as **Tailscale** or **WireGuard**), you do **not** open ports like `80`, `443`, or `11235` on your residential router. Port scanners (e.g., Shodan, Censys) will find your home IP completely dark and inaccessible.
- **Instant Replacement**: If a zero-day exploit or malicious flood compromises the VPS, your local infrastructure and home LAN remain completely untouched and isolated. A compromised VPS can be wiped and reprovisioned from automated scripts in minutes.

### 💰 Radical Cost Efficiency
Dedicated cloud servers with 16+ high-frequency CPU cores and 64GB RAM cost hundreds of dollars per month. Conversely:
- **Tiny VPS Specification**: A tiny 1 vCPU / 1GB RAM cloud VPS (available for $3 to $6/month from providers like Hetzner, IONOS, DigitalOcean, or Hostinger) is all that is required for the public gateway. Its sole function is TLS termination, reverse-proxying, and UDP packet forwarding.
- **Local Compute Heavy-Lifting**: Your existing desktop hardware or local server handles the CPU-intensive tasks: game simulation ticks, 1.0ms timer pacing, database persistence, and replay video rendering.
- **Zero Cloud Egress Surcharges**: High-bandwidth downloads (client patches, mods, assets) stream over your uncapped residential connection or cached through Cloudflare CDN.

---

## 🌐 3. Traffic Flow & Protocol Routing

Project Kongor V manages three distinct classes of network traffic:

| Traffic Class | Protocols & Ports | Gateway Routing Strategy |
|---|---|---|
| **Web API & Web UI** | `TCP 80, 443` (HTTP/HTTPS) | Cloudflare Proxied (`orange cloud`) ➔ VPS Reverse Proxy (Traefik / Caddy) ➔ Tailscale ➔ Local Master Server |
| **In-Game Chat Server** | `TCP 11031` | Direct VPS Reverse Proxy (Stream TCP) ➔ Tailscale Tunnel ➔ Local Chat Server |
| **Dedicated Game Servers** | `UDP 20000 - 29999` (COMPEL) | VPS Kernel `iptables` / `nftables` DNAT ➔ WireGuard Peer ➔ Local COMPEL Slaves |

### Cloudflare Edge Layer
Placing Cloudflare in front of web endpoints (`master.yourdomain.com`, `api.yourdomain.com`) provides:
1. **Free Wildcard SSL/TLS**: Cloudflare manages and auto-renews SSL certificates without touching your local machine.
2. **Web Application Firewall (WAF)**: Filters malicious bots, SQL injection patterns, and malformed HTTP floods.
3. **DDoS Absorption**: Cloudflare's massive edge capacity automatically mitigates Layer 7 attacks before traffic reaches your VPS.

---

## 🛠️ 4. Step-by-Step Implementation Guide

### Step 1: Provisioning the Sacrificial VPS
1. Rent a minimal Linux VPS (Ubuntu 24.04 LTS or Debian 12 recommended) located geographically near your physical server to minimize latency.
2. Harden the VPS:
   ```bash
   # Update packages and enable basic firewall
   sudo apt update && sudo apt upgrade -y
   sudo ufw default deny incoming
   sudo ufw default allow outgoing
   sudo ufw allow 22/tcp      # SSH
   sudo ufw allow 80/tcp      # HTTP
   sudo ufw allow 443/tcp     # HTTPS
   sudo ufw allow 11031/tcp   # HoN Chat Server
   sudo ufw allow 20000:20050/udp # COMPEL Dedicated Server UDP ports
   sudo ufw enable
   ```

### Step 2: Establish the Encrypted Tunnel (Tailscale or WireGuard)

#### Option A: Tailscale (Fastest Setup)
1. Install Tailscale on the VPS and your local server:
   ```bash
   curl -fsSL https://tailscale.com/install.sh | sh
   sudo tailscale up
   ```
2. On Windows (local server), install Tailscale from [tailscale.com](https://tailscale.com) and authenticate into the same tailnet.
3. Note the Tailscale IP of your local server (e.g., `100.x.y.z`).

#### Option B: WireGuard (Lowest Kernel Overhead)
For pure UDP game server forwarding, a direct point-to-point WireGuard tunnel delivers near-zero kernel latency.

### Step 3: Configure Reverse Proxy on VPS (Traefik / Caddy / Nginx)

Here is a streamlined Caddy configuration (`/etc/caddy/Caddyfile`) on the VPS forwarding web and API traffic through the tunnel:

```caddy
# Master Server API (Default Port 55555)
api.yourdomain.com {
    reverse_proxy 100.x.y.z:55555
}

# Web Portal & Administration UI (Default Port 5200 / 5266)
admin.yourdomain.com {
    reverse_proxy 100.x.y.z:5200
}
```

### Step 4: UDP Game Server Forwarding for COMPEL

Because game matches rely on low-latency UDP streams, route game server traffic directly at the Linux kernel level on the VPS using `iptables` or `nftables`:

```bash
# Enable IP forwarding on the VPS
sudo sysctl -w net.ipv4.ip_forward=1
echo "net.ipv4.ip_forward = 1" | sudo tee -a /etc/sysctl.d/99-forwarding.conf

# Forward COMPEL UDP ports 20000-20050 to the local server via Tailscale/WireGuard
VPS_IF="eth0"
TUNNEL_IF="tailscale0"
LOCAL_SERVER_IP="100.x.y.z"

sudo iptables -t nat -A PREROUTING -i $VPS_IF -p udp --dport 20000:20050 -j DNAT --to-destination $LOCAL_SERVER_IP
sudo iptables -A FORWARD -p udp -d $LOCAL_SERVER_IP --dport 20000:20050 -m state --state NEW,ESTABLISHED,RELATED -j ACCEPT
sudo iptables -t nat -A POSTROUTING -o $TUNNEL_IF -j MASQUERADE
```

### Step 5: Configure COMPEL Dedicated Server IP Advertisement

In your local Project Kongor V configuration (`src/apps/compel/appsettings.json` or CLI invocation):
- Configure `MatchServerManagerOptions` (and `IpAddressForClients` when behind a NAT/VPS gateway) to advertise the **public IP of your VPS** (not your home IP!).
- Match workers launched by COMPEL will report the gateway IP to the Master Server so connected clients route UDP packets through your VPS gateway.

```json
{
  "MatchServerManagerOptions": {
    "NumberOfInstances": 4,
    "PortOffset": 0,
    "EnableUdpProxy": true,
    "PriorityMode": "HighPriority"
  }
}
```

---

## 🩺 5. Verification and Troubleshooting

Verify your gateway setup with the following diagnostic tools:

```powershell
# Check local ecosystem ports and health
kongor doctor

# Test connectivity to your VPS gateway
Test-NetConnection -ComputerName vps.yourdomain.com -Port 443
Test-NetConnection -ComputerName vps.yourdomain.com -Port 11031

# Verify COMPEL slave startup with custom public IP
kongor host --port 20000
```

> [!TIP]
> **Timer Precision on Windows Hosts**: COMPEL automatically invokes `timeBeginPeriod(1)` to lock Windows thread scheduling to 1.0ms. When running over a VPS tunnel, ensure your local physical server has a wired Ethernet connection to your router to prevent wireless jitter.
