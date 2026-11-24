# ProjectKongor Network Ports & IPC Matrix Reference

This document provides the authoritative registry of all network ports, named pipes, and shared memory IPC channels used across the ProjectKongor ecosystem.

Use this reference when configuring firewalls, Docker port mappings, Kubernetes ingress/NodePort services, Aspire service bindings, and local development test environments.

---

## 1. Quick Reference: Port & Channel Allocation Table

| Port / Channel | Protocol | Subsystem | Direction / Scope | Default Usage & Description |
| :--- | :--- | :--- | :--- | :--- |
| **`11234`** | HTTP | Compel Local Fleet Control | Inbound LAN / Host | HTTP control plane & status probe for Local Windows COMPEL dedicated server supervisor (Offset 0). |
| **`11235`–`11334`** | UDP | Local Dedicated Workers | Inbound WAN/LAN | Local Windows dedicated server match worker game traffic (`hon_x64.exe -dedicated`, Offset 0, up to 100 instances). |
| **`11735`–`11834`** | UDP | Local Dedicated Voice | Inbound WAN/LAN | Local Windows dedicated server in-game VoIP channels (Offset 0, up to 100 instances). |
| **`21234`–`21334`** | UDP | Local Public Proxy Ports | Inbound WAN/LAN | Public-facing UDP proxy game & ping ports when `COMPEL_USE_PROXY=true` on Local Windows fleet. |
| **`21735`–`21834`** | UDP | Local Public Voice Proxy | Inbound WAN/LAN | Public-facing UDP proxy voice ports when `COMPEL_USE_PROXY=true` on Local Windows fleet. |
| **`11334`** | HTTP | Compel Docker Fleet Control | Inbound LAN / Host | HTTP control plane & status probe for Docker container COMPEL dedicated server supervisor (Offset 100). |
| **`11335`–`11434`** | UDP | Docker Dedicated Workers | Inbound WAN/LAN | Docker container dedicated server match worker game traffic (Offset 100, up to 100 instances). |
| **`11835`–`11934`** | UDP | Docker Dedicated Voice | Inbound WAN/LAN | Docker container dedicated server in-game VoIP channels (Offset 100, up to 100 instances). |
| **`21334`–`21434`** | UDP | Docker Public Proxy Ports | Inbound WAN/LAN | Public-facing UDP proxy game & ping ports when `COMPEL_USE_PROXY=true` in Docker container. |
| **`21835`–`21934`** | UDP | Docker Public Voice Proxy | Inbound WAN/LAN | Public-facing UDP proxy voice ports when `COMPEL_USE_PROXY=true` in Docker container. |
| **`11434`** (NodePort `31434`) | HTTP / TCP | Compel K3s Fleet Control | Inbound LAN / K8s | HTTP control plane for K3s Kubernetes COMPEL fleet (Offset 200, NodePort 31434). |
| **`11435`–`11483`** | UDP | K3s Dedicated Workers | Inbound WAN/LAN | K3s Kubernetes dedicated server match worker game traffic (Offset 200, up to 49 instances). |
| **`11935`–`11983`** | UDP | K3s Dedicated Voice | Inbound WAN/LAN | K3s Kubernetes dedicated server in-game VoIP channels (Offset 200, up to 49 instances). |
| **`21434`–`21483`** | UDP | K3s Public Proxy Ports | Inbound WAN/LAN | Public-facing UDP proxy game & ping ports when `COMPEL_USE_PROXY=true` in K3s cluster. |
| **`21935`–`21983`** | UDP | K3s Public Voice Proxy | Inbound WAN/LAN | Public-facing UDP proxy voice ports when `COMPEL_USE_PROXY=true` in K3s cluster. |
| **`11484`** | HTTP | Compel Queue Arcade Control | Inbound LAN / Host | HTTP control plane & status probe for Queue Arcade ("While You Wait") COMPEL fleet (Offset 250, Tier Q). |
| **`11485`–`11534`** | UDP | Queue Arcade Workers | Inbound WAN/LAN | Queue Arcade drop-in/drop-out mini-game worker traffic (Offset 250, Tier Q, up to 50 instances). |
| **`11985`–`12034`** | UDP | Queue Arcade Voice | Inbound WAN/LAN | Queue Arcade in-game VoIP channels (Offset 250, Tier Q, up to 50 instances). |
| **`21484`–`21534`** | UDP | Queue Arcade Proxy Ports | Inbound WAN/LAN | Public-facing UDP proxy game & ping ports when `COMPEL_USE_PROXY=true` on Queue Arcade fleet. |
| **`21985`–`22034`** | UDP | Queue Arcade Voice Proxy | Inbound WAN/LAN | Public-facing UDP proxy voice ports when `COMPEL_USE_PROXY=true` on Queue Arcade fleet. |
| **`11600`** | UDP | Voice Relay Service | Inbound WAN/LAN | COMPEL Voice Relay server switching Opus voice datagrams; sits safely in the 11535–11734 gap. |
| **`\\.\pipe\hon-server-{port}`** | IPC (Named Pipe) | Dedicated Server (`hon_x64.exe`) | Localhost Windows IPC | Dedicated server console management pipe (e.g. `\\.\pipe\hon-server-11235`). Used for deterministic cvar/command piping. |
| **`11245`** | UDP | Player Client (`hon_x64.exe`) | Inbound LAN/Host | Client listen server default port when hosting local practice matches without dedicated server. |
| **`11031`** | TCP | Chat Server (Master) | Inbound WAN/LAN | Master chat server connection handling user authentication, channels, and whispering. |
| **`11032`–`11033`** | TCP | Chat Server (Clustered Workers) | Inbound WAN/LAN | Clustered chat nodes for channel distribution and high player concurrency. |
| **`55555`** | TCP/HTTP | Master Server (Login & Matchmaking) | Inbound WAN/LAN (Dual-Stack) | Master server API for account authentication, match token issuance, and stats persistence (`masterserver.newbs.io` Cloudflare Tunnel origin). |
| **`5200`** | HTTP | Kongor Web API (`ZORGATH.WebPortal.API`) | Localhost (`127.0.0.1`) | REST API & Scalar/Swagger backend for player stats, match history, fleet administration, and diagnostics. |
| **`5201`** | HTTPS | Kongor Web API (`ZORGATH.WebPortal.API`) | Localhost (`127.0.0.1`) | Secure Kestrel HTTPS endpoint for Web API (`https://localhost:5201/swagger`). |
| **`5266`** | HTTP | Kongor Web UI (`DAWNBRINGER`) | Localhost / Tunnel (Dual-Stack) | Web frontend HTTP listener (`http://localhost:5266` & `newbs.io` Cloudflare Tunnel origin). |
| **`5557`** | HTTPS | Kongor Web UI (`DAWNBRINGER`) | Localhost / Tunnel (Dual-Stack) | Primary secure Web Portal UI listener (`https://localhost:5557` & `www.newbs.io` Cloudflare Tunnel origin). |
| **`5550`** | HTTPS | Aspire AppHost / Docker Dashboard | Localhost (`127.0.0.1`) | .NET Aspire orchestration dashboard (`https://localhost:5550`) providing metrics, traces, structured logs, and resource states in both Host and `kongor-stack` modes. |
| **`5551`** | HTTP | Aspire Dashboard (HTTP Fallback) | Localhost (`127.0.0.1`) | Localhost HTTP browser listener (`http://localhost:5551`) for the containerized Aspire Dashboard. |
| **`4317`** | gRPC | OpenTelemetry Collector | Localhost / Aspire | OTLP gRPC endpoint for receiving metrics, traces, and structured log streams. |
| **`4318`** | HTTP | OpenTelemetry Collector | Localhost / Aspire | OTLP HTTP endpoint for web/REST OpenTelemetry ingestion. |
| **`28298`** | TCP | Redis / Garnet Cache | Localhost / Aspire Internal | High-performance in-memory cache and message broker for game state, match tokens, and worker sync. |
| **`4040`** | HTTP | Pyroscope Continuous Profiler | Localhost / Internal LAN | Continuous CPU and memory profiling collector and flamegraph explorer. |
| **`\\.\pipe\willowmaker-ipc`** | IPC (Named Pipe) | Willowmaker Client Injector | Localhost Windows IPC | Dual-direction IPC pipe connecting Willowmaker's native agent DLL directly to the `hon_x64.exe` client process. |
| **`Local\HonReplayFrameBuffer_{pid}`** | Win32 Shared Memory | Vulkan Replay Capture Layer | Localhost (Inter-process) | Zero-copy triple-buffered RGBA/BGRA offscreen swapchain ring buffer for headless frame capture. |
| **`HonFrameReady_{pid}_{slot}`** | Win32 Event | Vulkan Replay Capture Layer | Localhost (Inter-process) | Waitable signaling event indicating a new offscreen frame is available in buffer slot `{slot}` (0, 1, or 2). |
| **`HonFrameDone_{pid}_{slot}`** | Win32 Event | Vulkan Replay Capture Layer | Localhost (Inter-process) | Waitable signaling event indicating the capture consumer has finished reading slot `{slot}`. |
| **`stdio` (JSON-RPC 2.0)** | Stdio Stream | TAO MCP Server | Process Stdio IPC | Standard I/O channel for AI assistant protocol interactions (Copilot, Claude, Gemini, Antigravity). |

---

## 2. Multi-Fleet Dedicated Server Deconfliction Architecture

COMPEL dedicated server fleets can run simultaneously across **Local Windows**, **Docker Containers**, and **K3s Kubernetes** on the same physical workstation or cluster with zero port contention or identity collisions.

### 2.1 Fleet Port Offset Matrix

COMPEL calculates its complete port plan dynamically via `PortPlan` using `COMPEL_PORT_RANGE_OFFSET`:
- **Local Game Port**: `11235 + PortRangeOffset + (instanceIndex - 1)`
- **Local Voice Port**: `11735 + PortRangeOffset + (instanceIndex - 1)`
- **Public Proxy Game Port**: `21235 + PortRangeOffset + (instanceIndex - 1)`
- **Public Proxy Voice Port**: `21735 + PortRangeOffset + (instanceIndex - 1)`
- **Control Plane Port**: `11234 + PortRangeOffset` (or explicit `COMPEL_CONTROL_PLANE_PORT`)

| Fleet Environment | Environment Badge | Port Offset | Control Plane Port | Local Game Ports (UDP) | Local Voice Ports (UDP) | Public Proxy Ports (UDP) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Local (Windows Native)** | `💻 LOCAL` | **`0`** | `11234` | `11235`–`11334` | `11735`–`11834` | `21234`–`21334` & `21735`–`21834` |
| **Docker Dedicated (Tier D)** | `🐳 DOCKER` | **`100`** | `11334` | `11335`–`11434` | `11835`–`11934` | `21334`–`21434` & `21835`–`21934` |
| **K3s Kubernetes** | `☸️ K3S` | **`200`** | `11434` (NodePort `31434`) | `11435`–`11483` | `11935`–`11983` | `21434`–`21483` & `21935`–`21983` |
| **Queue Arcade (Tier Q)** | `🕹️ ARCADE` | **`250`** | `11484` | `11485`–`11534` | `11985`–`12034` | `21484`–`21534` & `21985`–`22034` |

### 2.2 100-Port Tier Partition & Ecosystem Deconfliction
Under the 100-port tier partition model, each fleet tier is partitioned into 100 reserved ports:
- **Local Fleet (Offset 0)**: Up to 100 worker instances (`11235`–`11334`), voice (`11735`–`11834`).
- **Docker Fleet (Offset 100)**: Up to 100 worker instances (`11335`–`11434`), voice (`11835`–`11934`).
- **K3s Kubernetes (Offset 200)**: Up to 100 worker instances (`11435`–`11534`), voice (`11935`–`12034`).
- **Voice Relay Service**: Dedicated UDP voice switching at **`11600`**, positioned directly in the gap between K3s game ports (`11534`) and Local voice ports (`11735`).
- **Client Listen Server**: When a player hosts an offline bot or practice match without a dedicated server, `hon_x64.exe` client binds listen server port `11245 UDP`. In multi-tier containerized/K3s fleets, dedicated servers operate on separate offsets or host networks.

### 2.3 UDP Proxy & Anti-DDoS Architecture (`20000`–`29999`)
When `COMPEL_USE_PROXY=true`:
- Worker processes bind locally (e.g. `11235`, `11335`, `11435`) and are shielded from direct WAN exposure.
- `UDPProxyService` listens on public ports offset by +10000 (e.g. `21235`, `21335`, `21435` for game, and `21735`, `21835`, `21935` for voice).
- Inbound traffic passes through `UdpRateLimiter` (lock-free 64-bit atomic token-bucket filter) to protect match servers from amplification attacks and malformed UDP packet flooding.

---

## 3. Dedicated Server Port Stepping & Named Pipe IPC

Every dedicated worker instance (`hon_x64.exe -dedicated`) is launched with its discrete listen port:

```powershell
# Local Windows Instance 1 (Port 11235)
hon_x64.exe -dedicated -port 11235 -voiceport 11735 -masterserver 127.0.0.1:55555 -execute "Set svr_name 'KONGOR ARENA #1'"

# Docker Container Instance 1 (Port 11335)
hon_x64.exe -dedicated -port 11335 -voiceport 11835 -masterserver 127.0.0.1:55555 -execute "Set svr_name 'KONGOR ARENA #D1'"
```

### Dedicated Named Pipe Isolation
Each worker instance exposes a unique named pipe matching its port:
- Port `11235`: `\\.\pipe\hon-server-11235`
- Port `11236`: `\\.\pipe\hon-server-11236`
- Port `11335`: `\\.\pipe\hon-server-11335`
- Port `11336`: `\\.\pipe\hon-server-11336`
- Port `11435`: `\\.\pipe\hon-server-11435`
- Port `11436`: `\\.\pipe\hon-server-11436`

Piping commands via `kongor_game_server_command` or COMPEL standard input routes deterministically to the target worker using its port.

---

## 4. Container & Orchestration Port Mappings

### 4.0 Turnkey Full-Stack Compose ([`infra/docker/docker-compose.stack.yml`](../../infra/docker/docker-compose.stack.yml))

Orchestrated via `.\kongor.ps1 docker up` ([`scripts/docker/launch_docker_stack.ps1`](../../scripts/docker/launch_docker_stack.ps1)), the `kongor-stack` project runs all 8 core services (plus optional `postgres:25432` under `--local-postgres`) on a shared `kongor-stack-net` bridge network with full **"Start Cluster"** port and telemetry parity:

- **Observability & Infrastructure**: `aspire-dashboard` (`127.0.0.1:5550` HTTPS UI, `127.0.0.1:5551` HTTP UI, internal OTLP gRPC `:18889` receiving logs/traces/metrics from all 6 `.NET` services) and `garnet` (`127.0.0.1:28298/tcp`).
- **Backend Cluster & Dual-Stack Cloudflare Tunnel Origins**: `master-server` (`55555/tcp` dual-stack IPv4+IPv6), `chat-server` (`127.0.0.1:5554/tcp` HTTP health + `11031-11033/tcp` K2 chat framing), `web-api` (`127.0.0.1:5200/tcp` HTTP + `127.0.0.1:5201/tcp` HTTPS), and `web-ui` (`5266/tcp` HTTP + `5557/tcp` HTTPS dual-stack IPv4+IPv6 for `www.newbs.io`, `newbs.io`, and `--testing-domain` tunnels, plus internal SignalR `http://web-ui:5266/hubs/live-matches`).
- **Dedicated Match Fleet (`compel-dedicated`, Offset `100`, Tier D)**: Control plane `127.0.0.1:11334/tcp`, Game UDP `11335-11342/udp`, Voice UDP `11835-11842/udp`.
- **Queue Arcade Fleet (`compel-queue-arcade`, Offset `250`, Tier Q)**: Control plane `127.0.0.1:11484/tcp`, Game UDP `11485-11492/udp`, Voice UDP `11985-11992/udp`. Isolated from public `f=server_list` (`gameType=10/90`) and `f=create_game`, and gated in `f=c_conn` by coordinator-issued Garnet `ARCADE-ALLOW:{serverId}:{accountId}` tickets so only TMM-routed players (or `Staff`) can join.
- **Split-Horizon LAN & WAN Player Routing (`CHAT_SERVER_LAN_HOST` + `KONGOR_DISCOVER_PUBLIC_IP`)**: `launch_docker_stack.ps1` writes the host machine's routed LAN IPv4 address into `userdata/docker/.env.stack` while `master-server` and `chat-server` dynamically discover the host's public WAN IPv4 (`KONGOR_DISCOVER_PUBLIC_IP=true`). External WAN players arriving through `masterserver.newbs.io` (`CF-Connecting-IP`) receive the public WAN IP for Chat (`:11031`) and Dedicated Match UDP (`:11335+`), while local/LAN/NAT-hairpin players receive the host LAN IP.
- **Container Loopback Bridges ([`infra/docker/entrypoint.sh`](../../infra/docker/entrypoint.sh))**: Inside COMPEL containers, `socat` forwards `127.0.0.1:11032-11033` $\to$ `chat-server` and `127.0.0.1:55555` $\to$ `master-server` (for `Configuration.CDN.Host` `.honreplay` uploads).

### 4.1 Standalone Docker Container (`compel-dedicated-server`, Offset 100)

Launch command built by `ContainerLaunchCommandBuilder` and [`infra/docker/run_docker_servers.ps1`](../../infra/docker/run_docker_servers.ps1):

```powershell
docker run -d `
  --name compel-dedicated-server `
  --restart unless-stopped `
  --add-host=host.docker.internal:host-gateway `
  -p 11334:11334 `
  -p 11335-11336:11335-11336/udp `
  -p 11835-11836:11835-11836/udp `
  -p 21334-21336:21334-21336/udp `
  -p 21835-21836:21835-21836/udp `
  --cap-add=SYS_NICE `
  -e MASTER_SERVER_ADDRESS="http://host.docker.internal:55555" `
  -e COMPEL_GATEWAY="127.0.0.1" `
  -e COMPEL_PRIORITY_MODE=Background `
  -e COMPEL_INSTANCES=2 `
  -e COMPEL_LOCATION="USE" `
  -e COMPEL_SERVER_NAME_PREFIX="^c[DOCKER] ^oKONGOR ARENA ^r" `
  -e COMPEL_PORT_RANGE_OFFSET=100 `
  -e COMPEL_CONTROL_PLANE_PORT=11334 `
  -e COMPEL_USE_PROXY=true `
  -e HOST_USERNAME="YOUR_KONGOR_USERNAME" `
  -e HOST_PASSWORD="YOUR_KONGOR_PASSWORD" `
  -e COMPEL_AUTHENTICATION_TOKEN="YOUR_COMPEL_AUTH_TOKEN" `
  -v "${PWD}/games/server:/app/server:ro" `
  -v "${PWD}/artefacts:/app/artefacts:rw" `
  projectkongor/compel-dedicated:latest
```

### 4.2 Docker Compose (`infra/docker/docker-compose.community.yml`)

```yaml
services:
  compel-dedicated:
    build:
      context: ../..
      dockerfile: infra/docker/Dockerfile.dedicated
    image: projectkongor/compel-dedicated:latest
    container_name: compel-dedicated-server
    restart: unless-stopped
    network_mode: host
    environment:
      - COMPEL_INSTANCES=2
      - COMPEL_PRIORITY_MODE=Background
      - HOST_USERNAME=${HOST_USERNAME:-YOUR_HOST_USERNAME}
      - HOST_PASSWORD=${HOST_PASSWORD:-YOUR_HOST_PASSWORD}
      - COMPEL_LOCATION=${COMPEL_LOCATION:-USE}
      - COMPEL_SERVER_NAME_PREFIX=${COMPEL_SERVER_NAME_PREFIX:-^c[DOCKER] ^oKONGOR ARENA ^r}
      - COMPEL_PORT_RANGE_OFFSET=${COMPEL_PORT_RANGE_OFFSET:-100}
      - COMPEL_CONTROL_PLANE_PORT=${COMPEL_CONTROL_PLANE_PORT:-11334}
      - COMPEL_USE_PROXY=true
      - COMPEL_AUTHENTICATION_TOKEN=${COMPEL_AUTHENTICATION_TOKEN:-YOUR_COMPEL_AUTH_TOKEN}
    volumes:
      - /opt/hon/server:/app/server:ro
      - ./artefacts:/app/artefacts:rw
    cap_add:
      - SYS_NICE
```

### 4.3 K3s Kubernetes Service & Deployment (`infra/k8s/compel-k3s.yaml`, Offset 200)

```yaml
apiVersion: v1
kind: Service
metadata:
  name: compel-k3s-service
  namespace: default
spec:
  type: NodePort
  selector:
    app: compel-k3s
  ports:
    - name: control-tcp
      port: 11434
      targetPort: 11434
      nodePort: 31434
      protocol: TCP
    - name: game-1-udp
      port: 11435
      targetPort: 11435
      nodePort: 31435
      protocol: UDP
    - name: game-2-udp
      port: 11436
      targetPort: 11436
      nodePort: 31436
      protocol: UDP
    - name: voice-1-udp
      port: 11935
      targetPort: 11935
      nodePort: 31935
      protocol: UDP
    - name: voice-2-udp
      port: 11936
      targetPort: 11936
      nodePort: 31936
      protocol: UDP
    - name: proxy-1-udp
      port: 21435
      targetPort: 21435
      nodePort: 41435
      protocol: UDP
    - name: proxy-2-udp
      port: 21436
      targetPort: 21436
      nodePort: 41436
      protocol: UDP
    - name: proxy-voice-1-udp
      port: 21935
      targetPort: 21935
      nodePort: 41935
      protocol: UDP
    - name: proxy-voice-2-udp
      port: 21936
      targetPort: 21936
      nodePort: 41936
      protocol: UDP
```

---

## 5. High-Speed Inter-Process Communication (IPC)

### 5.1 Vulkan Offscreen Capture Ring Buffer
Headless testing uses a high-performance zero-copy memory-mapped file for real-time visual assertions:
- **Shared Memory Handle**: `Local\HonReplayFrameBuffer_{pid}`
- **Triple-Buffering Architecture**:
  - Slot 0: `0`
  - Slot 1: `1`
  - Slot 2: `2`
- **Synchronization Primitives**:
  - `HonFrameReady_{pid}_{slot}`: Signaled by Vulkan layer when rendering completes.
  - `HonFrameDone_{pid}_{slot}`: Signaled by consumer (test harness or MCP server) when frame reading finishes.
- **Latency / Performance**: 0 ms dwell time; no desktop window activation; zero GDI/DWM screen scrape overhead.

### 5.2 Willowmaker Client Pipe
- **Named Pipe**: `\\.\pipe\willowmaker-ipc`
- **Protocol**: Binary length-prefixed JSON packets.
- **Functionality**:
  - Executing in-engine Lua scripts via `GameLuaHandler`.
  - Polling game engine state, cvar values, and active match entities.
  - Injecting input simulation events deterministically without foreground window focus.

---

## 6. Security & Firewall Recommendations

1. **Keep Internal Ports Private**: Ports `28298` (Redis/Garnet), `5200` (Internal API), `4040` (Pyroscope), `4317`/`4318` (OTLP), and `5550` (Aspire Dashboard) should **never be exposed to the public Internet**. Restrict them to `localhost` or private virtual networks.
2. **Public Facing Services**:
   - `55555` (Master Server / Auth) — protect with TLS termination and reverse proxy (e.g., Cloudflare, NGINX).
   - `11031`–`11033` (Chat) — rate-limit inbound connection floods to prevent denial-of-service.
   - `21235`–`21534` (Game Proxy Ports) & `21735`–`22034` (Voice Proxy Ports) — open UDP ports on host firewalls for client gameplay and in-game voice.
   - Control Plane Ports (`11234`, `11334`, `11434`, `11484`) — require `AuthenticationToken` bearer authentication header for all administrative actions (`/sync`, `/start`, `/stop`, `/restart`, `/command`).
3. **Local Testing Isolation**: On Windows developer workstations, ensure Windows Defender Firewall allows outbound and inbound connections for `hon_x64.exe` on private networks.
