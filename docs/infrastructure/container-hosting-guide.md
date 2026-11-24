# 🎮 The Ultimate Project KONGOR Server Hosting Guide
### (Docker, Minikube, & Kubernetes / Agones)

> **Goal:** Host your own Heroes of Newerth dedicated game servers effortlessly. Whether you are running a single practice server on your gaming PC without dropping a single frame, or scaling a 50-server tournament fleet in the cloud, this guide gives you **ELI5 explanations** and **ready-to-run copy-paste commands**.

---

## 🧭 Part 1: Which Tool Should You Use? (ELI5)

| Tool | Who is it for? | ELI5 Explanation | Best Priority Mode |
| :--- | :--- | :--- | :--- |
| **Docker** 🐳 | **95% of Hosts** (Home PCs, Laptops, single VPS) | Like installing a portable app. Everything is bundled inside one box. Just type one command and your server is live. | **`Background`** or **`Adaptive`** |
| **Minikube** ☸️ | **Devs & Testers** testing Kubernetes locally | Like a miniature cloud playground running on your personal computer. It lets you test Kubernetes commands without paying cloud bills. | **`Background`** or **`HighPriority`** |
| **Kubernetes (Agones)** ☁️ | **Tournaments & Cloud Clusters** (GKE, EKS, Bare-Metal) | Like an airport air traffic control tower. It automatically manages dozens of servers, restarts crashed ones, and scales with player demand. | **`HighPriority`** |

---

## ⚡ Part 2: The 3 Priority Modes Explained (ELI5)

When you run a dedicated game server, COMPEL manages how your CPU treats the server:

### 1. 🍃 Background / Efficiency Mode (`COMPEL_PRIORITY_MODE=Background`)
* **ELI5:** **"The Polite Roommate."**
* **How it works:** It actively yields CPU priority (`BelowNormal` priority / Linux `nice +10` / `cpu.weight = 50`). On modern Intel/AMD chips, it steers the server onto **Efficiency Cores (E-Cores)** and activates Windows 11 **EcoQoS** (green leaf mode).
* **The Result:** Your computer feels completely untouched. You can play HoN at 240 FPS, stream, or compile code while your PC hosts a full 5v5 teamfight in the background with **0% desktop disruption and 0 frame drops**.

### 2. 🚀 High-Priority Mode (`COMPEL_PRIORITY_MODE=HighPriority`)
* **ELI5:** **"The Tournament Champion."**
* **How it works:** It does **not** yield (`High` priority / Linux `nice -10` / `cpu.weight = 1000`). It automatically detects your hardware and pins game servers to the **"Best Processors Available"**:
  - **AMD Ryzen (7000/9000X3D):** Detects and pins to the **3D V-Cache CCD (CCD0 with 96 MB L3 cache)**.
  - **Intel Core:** Pins strictly to **physical Performance Cores (P-Cores)**, eliminating hyperthreading (SMT) elbow-bumping.
* **The Result:** Tournament-grade, sub-millisecond tick stability. Ideal for dedicated tournament boxes and cloud fleets.

### 3. 🦎 Adaptive Mode (`COMPEL_PRIORITY_MODE=Adaptive`)
* **ELI5:** **"The Smart Chameleon."**
* **How it works:** It constantly watches your machine:
  - When you are **away / not playing**, it runs at **HighPriority** to give community players max performance.
  - The exact split-second you launch your game client (`hon_x64.exe`), it automatically shifts down to **Background** mode so your personal game gets 100% of your PC's power. When you quit, it ramps back up!

---

## 🔑 Part 3: The 3 Host Credentials from the Staff Portal (ELI5)

When your host application is approved in the **Project KONGOR Staff Portal** (or in Account Settings), you are issued **3 essential credentials**:

| Credential | Environment Variable | What It Does (ELI5) | Is It Mandatory? |
| :--- | :--- | :--- | :--- |
| **Host Username** | `HOST_USERNAME` | The account name for your dedicated server bot. Players see this in the public server list. | **YES** |
| **Host Password** | `HOST_PASSWORD` | The password the game server (`hon_x64.exe`) uses to log in to the Master Server (`masterserver.newbs.io`). | **YES** |
| **COMPEL Auth Token** | `COMPEL_AUTHENTICATION_TOKEN` | The bearer token (starts with `tok_...`) that locks down COMPEL's remote HTTP Control Plane (`http://localhost:11234`, or `5555` if overridden via `COMPEL_CONTROL_PORT`). | **YES** (Crucial in Production) |

### ⚠️ Why Do You Need the Auth Token?
1. **Security Guardrail:** COMPEL's Control Plane lets you restart servers, send console commands, and query telemetry. In production (`DOTNET_ENVIRONMENT=Production`), **COMPEL will strictly refuse to start if this token is missing** so nobody on the internet can hijack your server.
2. **Staff Portal Fleet Management:** The Staff Portal uses this token to communicate with your host, monitor health, and coordinate matchmaking queues.
3. **Admin Commands:** You will use this token to authenticate `curl` commands (like adding bots or restarting workers).

---

## 🚀 Turnkey Full-Stack Local/LAN Launcher (`kongor docker`)

If you want to launch **everything** on a single developer workstation or LAN server—the full backend cluster (`aspire-dashboard`, `garnet`, `master-server`, `chat-server`, `web-api`, `web-ui`), Dedicated Match Fleet (`compel-dedicated` `:11334`), Queue Arcade Fleet (`compel-queue-arcade` `:11484`), and optional native Windows game client (`hon_x64.exe` or `WILLOWMAKER`) with 1:1 **"Start Cluster"** parity—use the turnkey CLI orchestrator ([`scripts/docker/launch_docker_stack.ps1`](../../scripts/docker/launch_docker_stack.ps1) / [`infra/docker/docker-compose.stack.yml`](../../infra/docker/docker-compose.stack.yml)):

```powershell
# 1. Safely rotate any placeholder/dev-default secrets (creates a .bak backup of secrets.json first)
.\kongor.ps1 docker init-secrets

# 2. Run fail-closed pre-flight validation (secrets, HTTPS dev cert, ports, Docker daemon, server binaries)
.\kongor.ps1 docker check

# 3. Launch the complete stack (Eco | Balanced | High) + native Windows game client
.\kongor.ps1 docker up --perf Balanced --client

# 3a. Force re-publish linux-x64 binaries, rebuild images, clear host conflicts, and launch:
.\kongor.ps1 docker rebuild

# 3b. One-shot full workflow (check -> stop docker & host conflicts -> rebuild & up -> full status verification):
.\kongor.ps1 docker sync

# 3c. Optional: Register an extra preview/testing domain (CORS + TLS SAN) or enable offline username/password Web Portal auth
.\kongor.ps1 docker up --perf Balanced --testing-domain preview.example.test
.\kongor.ps1 docker up --perf Eco --standalone-auth

# 4. Inspect unified container + COMPEL fleet health or tail logs
.\kongor.ps1 status
.\kongor.ps1 docker logs -s compel-dedicated -f

# 5. Stop the Docker stack cleanly
.\kongor.ps1 stop docker
```

| Preset (`--perf`) | Match Workers (`:11335+`) | Queue Arcade Workers (`:11485+`) | `.NET GC` | `COMPEL_PRIORITY_MODE` | Ideal Hardware |
| :--- | :---: | :---: | :--- | :--- | :--- |
| **`Eco`** | `1` (`1` warm) | `1` (`1` warm) | Workstation (`Conserve=7`) | `Background` | Laptops & background dev (`~6 GB` cap) |
| **`Balanced`** *(default)* | `2` (`1` warm) | `2` (`1` warm) | Workstation (`Conserve=3`) | `Adaptive` | 16–32 GB dev/gaming PCs |
| **`High`** | `4` (`2` warm) | `2` (`2` warm) | Server GC | `HighPriority` | 32–64 GB+ workstations & LAN hosts |

> [!TIP]
> **GUI Control:** You can also manage the full Docker stack from **`NEXUS.ControlPanel.GUI`** (`.\kongor.ps1 gui`) via the **`🐳 Docker Stack ▾`** toolbar menu, with live `[kongor-stack: 8/8 running]` status and `logs/docker/<service>/` tailing.

### 🌐 "Start Cluster" Endpoint, Cloudflare Tunnel (`newbs.io`), & Testing Domain Parity
- **Identical Localhost & Tunnel Ports:** `kongor-stack` binds the exact same ports as `ASPIRE.ApplicationHost` ("Start Cluster"):
  - **Aspire Dashboard + OTLP Collector:** `https://localhost:5550` & `http://localhost:5551` (receiving live OpenTelemetry logs, traces, and metrics over internal gRPC `:18889` from all 6 `.NET` containers).
  - **Web Portal (`DAWNBRINGER`):** `https://localhost:5557` & `http://localhost:5266` (published dual-stack IPv4 + IPv6 `[::]` so host `Cloudflared` tunnels resolving `localhost` to `[::1]` connect seamlessly for `www.newbs.io` and `newbs.io`).
  - **Web API (`ZORGATH`):** `https://localhost:5201/swagger` & `http://localhost:5200/swagger` (`127.0.0.1` host-only).
  - **Master Server (`KONGOR`):** `http://localhost:55555` (published dual-stack IPv4 + IPv6 `[::]` for `masterserver.newbs.io` and local K2 clients).
- **Automatic Trusted HTTPS Certificate Mounting:** `launch_docker_stack.ps1` exports the host's trusted `dotnet dev-certs https` certificate to `userdata/docker/aspnetcore-devcert.pfx` (falling back to a pure-.NET SAN certificate covering `localhost`, `newbs.io`, `*.newbs.io`, and `--testing-domain` if `dotnet dev-certs` is unavailable) and mounts it read-only into `aspire-dashboard`, `web-ui`, and `web-api`.
- **Multi-Hop Forwarded Headers & Testing Domains (`--testing-domain`):** [`ServiceExtensions.cs`](../../src/libs/service-defaults/ServiceExtensions.cs) (`ConfigureInfrastructureForwardedHeaders` + `UseInfrastructureForwardedHeaders`) normalizes IPv4-mapped IPv6 Docker bridge peers (`::ffff:172.16.0.0/12`), honors `X-Forwarded-Proto`, `X-Forwarded-Host`, `CF-Connecting-IP`, and `CF-Visitor` across up to 4 proxy hops, and expands CORS/SAN allowlists when `--testing-domain <domain>` (or `TESTING_DOMAIN`) is supplied so Discord/Twitch OAuth callbacks (`https://<domain>/signin-discord`) work across `localhost:5557`, `www.newbs.io`, and any preview tunnel domain simultaneously.
- **Auth & Economy Parity (`--standalone-auth`):** By default, `KONGOR_STANDALONE_WEB=false` matches "Start Cluster" (Discord OAuth Web Portal gate + standard registration economy). Pass `--standalone-auth` (`-StandaloneAuth`) only when running an isolated offline sandbox that requires local username/password Web Portal login.
- **Split-Horizon LAN & External WAN Player Routing:** `master-server` and `chat-server` run with `KONGOR_DISCOVER_PUBLIC_IP=true` while preserving local host detection (`PUBLIC_GAME_SERVER_IP=127.0.0.1` + `CHAT_SERVER_LAN_HOST`), allowing external WAN players arriving via `masterserver.newbs.io` (`CF-Connecting-IP`) to receive your public WAN IP for Chat (`:11031`) and Dedicated Match UDP (`:11335+`) while local/LAN/NAT-hairpin players seamlessly receive your LAN IP.

### 🔒 Queue Arcade (`Tier Q`) Isolation & Join Authorization
- **Hidden from Public Pickers:** Queue Arcade workers (`<stem>_arc` host accounts, COMPEL Tier Q `Instance` `4000..4999`, UDP `11485–11534` / proxy `21485–21534`) are strictly isolated from player lobby lists (`f=server_list` `gameType=10` & `gameType=90`) and `f=create_game` auto-selection ([`MatchServer.cs`](../../src/libs/protocol-core/Models/ServerManagement/MatchServer.cs), [`ServerListHandler.cs`](../../src/services/master-server/Handlers/ClientRequester/ServerListHandler.cs), [`GameHandler.cs`](../../src/services/master-server/Handlers/ClientRequester/GameHandler.cs)), while remaining visible to operators in `NEXUS.ControlPanel.GUI`, `kongor status`, and TAO MCP tools.
- **Coordinator-Gated Joins (`ARCADE-ALLOW`):** Non-staff players cannot hijack Queue Arcade instances via raw console `Connect <ip>:11485`, server invites (`0x0023`/`0x0024`), non-staff spectate (`0x00B8`), or player-hosted lobby creation (`MatchType == 0`). [`QueueArcadeCoordinatorService.cs`](../../src/services/chat-server/Services/QueueArcadeCoordinatorService.cs) mints a short-lived Garnet `ARCADE-ALLOW:{serverId}:{accountId}` ticket before dispatching `0x0062`, which [`ServerRequesterController.Authentication.cs`](../../src/services/master-server/Controllers/ServerRequesterController/ServerRequesterController.Authentication.cs) enforces on `f=c_conn` (staff accounts remain permitted for live diagnostics).

---

## 🐳 Option A: Standalone Volunteer Hosting with Docker (`docker-compose.community.yml`)

### Step 1: Build the Image (One-Time)
Run this from the ProjectKongor repository root:
```powershell
docker build -f infra/docker/Dockerfile.dedicated -t projectkongor/compel-dedicated:latest .
```

---

### Step 2: Choose Your Mode & Run

#### Mode 1: Background / Efficiency Mode (Default for Home PCs)
*Leaves your computer 100% smooth while you game.*

**Using Docker Compose:**
```powershell
# In infra/docker/docker-compose.community.yml, set COMPEL_PRIORITY_MODE=Background
docker compose -f infra/docker/docker-compose.community.yml up -d
```

**Or as a Single `docker run` Command:**
```powershell
docker run -d `
  --name compel-server-bg `
  --restart unless-stopped `
  --net=host `
  --cap-add=SYS_NICE `
  -e COMPEL_PRIORITY_MODE=Background `
  -e COMPEL_INSTANCES=2 `
  -e HOST_USERNAME="YOUR_KONGOR_USERNAME" `
  -e HOST_PASSWORD="YOUR_KONGOR_PASSWORD" `
  -e COMPEL_AUTHENTICATION_TOKEN="YOUR_COMPEL_AUTH_TOKEN" `
  -v /opt/hon/server:/app/server:ro `
  -v ./artefacts:/app/artefacts:rw `
  projectkongor/compel-dedicated:latest
```

---

#### Mode 2: High-Priority Mode (For Dedicated Server PCs)
*Gives maximum tick stability for tournaments and dedicated boxes.*

**Using Docker Compose:**
Change `COMPEL_PRIORITY_MODE=HighPriority` in `docker-compose.community.yml`, then:
```powershell
docker compose -f infra/docker/docker-compose.community.yml up -d
```

**Or as a Single `docker run` Command:**
```powershell
docker run -d `
  --name compel-server-high `
  --restart unless-stopped `
  --net=host `
  --cap-add=SYS_NICE `
  -e COMPEL_PRIORITY_MODE=HighPriority `
  -e COMPEL_INSTANCES=2 `
  -e HOST_USERNAME="YOUR_KONGOR_USERNAME" `
  -e HOST_PASSWORD="YOUR_KONGOR_PASSWORD" `
  -e COMPEL_AUTHENTICATION_TOKEN="YOUR_COMPEL_AUTH_TOKEN" `
  -v /opt/hon/server:/app/server:ro `
  -v ./artefacts:/app/artefacts:rw `
  projectkongor/compel-dedicated:latest
```

---

#### Mode 3: Adaptive Mode (The Smart Auto-Switching Host)
*High-Priority when idle; drops to Background when you open the game.*

**Using Docker Compose:**
Set `COMPEL_PRIORITY_MODE=Adaptive` in `docker-compose.community.yml`, then:
```powershell
docker compose -f infra/docker/docker-compose.community.yml up -d
```

**Or as a Single `docker run` Command:**
```powershell
docker run -d `
  --name compel-server-adaptive `
  --restart unless-stopped `
  --net=host `
  --cap-add=SYS_NICE `
  -e COMPEL_PRIORITY_MODE=Adaptive `
  -e COMPEL_INSTANCES=2 `
  -e HOST_USERNAME="YOUR_KONGOR_USERNAME" `
  -e HOST_PASSWORD="YOUR_KONGOR_PASSWORD" `
  -e COMPEL_AUTHENTICATION_TOKEN="YOUR_COMPEL_AUTH_TOKEN" `
  -v /opt/hon/server:/app/server:ro `
  -v ./artefacts:/app/artefacts:rw `
  projectkongor/compel-dedicated:latest
```

---

## ☸️ Option B: Hosting on Minikube (Local Kubernetes Playground)

Minikube allows you to test Kubernetes on your personal laptop or desktop without needing a paid cloud account.

### Step 1: Start Minikube
```powershell
minikube start --driver=docker --cpus=4 --memory=4096
```

### Step 2: Load Your Built Docker Image Into Minikube
```powershell
minikube image load projectkongor/compel-dedicated:latest
```

---

### Step 3: Run by Priority Mode

#### Mode 1: High-Priority Mode on Minikube
Create or run [`infra/k8s/local-test-pod.yaml`](../../infra/k8s/local-test-pod.yaml):
```powershell
kubectl apply -f infra/k8s/local-test-pod.yaml
```
*To verify that Minikube applied maximum CPU weight:*
```powershell
kubectl exec -it compel-test-highpriority -- cat /sys/fs/cgroup/cpu.weight
# Should output: 1000
```

---

#### Mode 2: Background / Efficiency Mode on Minikube
Run an ad-hoc pod in Background mode:
```powershell
kubectl run compel-minikube-bg `
  --image=projectkongor/compel-dedicated:latest `
  --image-pull-policy=Never `
  --env="COMPEL_PRIORITY_MODE=Background" `
  --env="COMPEL_INSTANCES=1" `
  --env="HOST_USERNAME=MY_USER" `
  --env="HOST_PASSWORD=MY_PASS" `
  --env="COMPEL_AUTHENTICATION_TOKEN=MY_AUTH_TOKEN"
```
*To verify that Minikube applied background yielding weight:*
```powershell
kubectl exec -it compel-minikube-bg -- cat /sys/fs/cgroup/cpu.weight
# Should output: 50
```

---

#### Mode 3: Adaptive Mode on Minikube
```powershell
kubectl run compel-minikube-adaptive `
  --image=projectkongor/compel-dedicated:latest `
  --image-pull-policy=Never `
  --env="COMPEL_PRIORITY_MODE=Adaptive" `
  --env="COMPEL_INSTANCES=1" `
  --env="HOST_USERNAME=MY_USER" `
  --env="HOST_PASSWORD=MY_PASS" `
  --env="COMPEL_AUTHENTICATION_TOKEN=MY_AUTH_TOKEN"
```

---

## ☁️ Option C: Cloud Kubernetes & Agones Fleets (Tournaments & Scaled Clusters)

For production game server clusters in Google Cloud (GKE), AWS (EKS), or on-prem bare-metal Kubernetes.

### Step 1: Configure Your Secret Credentials
Create your host authentication secret in Kubernetes:
```powershell
kubectl create secret generic kongor-host-credentials `
  --from-literal=username="MY_REGISTERED_KONGOR_HOST" `
  --from-literal=password="MY_SECURE_PASSWORD" `
  --from-literal=token="MY_COMPEL_CONTROL_PLANE_TOKEN"
```

### Step 2: Deploy the Production Agones Fleet
Apply the fleet configuration:
```powershell
kubectl apply -f infra/k8s/agones/fleet.yaml
```

### Step 3: Monitor the Fleet
```powershell
# Inspect running game server pods
kubectl get pods -l app=kongor-dedicated

# Tail logs to see P-Core / 3D V-Cache pinning
kubectl logs -l app=kongor-dedicated --tail=50 -f
```

---

## 🎯 Part 4: How to Easily Join and Play on Your Hosted Server

Once your container or pod is running, here is how you and your friends connect:

### 1. Find Your Server's IP Address
- **Local Host:** `127.0.0.1` (or your LAN IP, e.g. `192.168.1.50`).
- **VPS / Cloud:** Your public server IP (e.g. `198.51.100.25`).

### 2. Connect from the HoN Game Client
1. Open *Heroes of Newerth*.
2. Press **Ctrl + F8** to open the K2 console.
3. Type:
   ```
   Connect <Your_IP>:11235
   ```
   *(Use `:11335` for Docker Dedicated Tier D, or `:21235` / `:21335` when `COMPEL_USE_PROXY=true`. Note: Queue Arcade Tier Q ports `11485–11534` are coordinator-gated via `ARCADE-ALLOW` tickets and reject manual non-staff console connects.)*
4. Press Enter. You are instantly in the match!

---

## 🛠️ Part 5: Useful Admin Commands

### Add Bots / Fake Players for Testing
Pipe console commands directly to dedicated server port `11235`:
```powershell
# Via the COMPEL Control Plane REST API (default port 11234, or 5555 if COMPEL_CONTROL_PORT=5555):
curl -X POST http://localhost:11234/api/servers/11235/command `
  -H "Authorization: Bearer YOUR_TOKEN" `
  -H "Content-Type: application/json" `
  -d '{"command":"AddFakePlayer 1"}'
```

### Check Server Health & CPU Topology Status
```powershell
curl http://localhost:11234/healthz
# (or http://localhost:11234/health)
```

### Inspect Container Logs
```powershell
docker logs compel-dedicated-server --tail 30
```
Look for the confirmation line:
```
[CpuTopologyResolver] Assigned CPU Affinity Mask 0x... (Primary Core: 0, P-Core: True, 3D V-Cache: True, Priority: HighPriority)
```

---

## Demand-Driven Queue Arcade Lifecycle & Gated Deep RL Self-Play

By default (`QueueArcade:IdleMode = Hibernate`), Queue Arcade servers (`Tier Q`) remain warm in `SERVER_STATUS_IDLE` (~0% CPU) when **0 opted-in players** are logged into Chat/Lobby in that region:

- **Pre-Warm on Lobby Presence:** As soon as any opted-in player enters Chat/Lobby, `#Q1` wakes up immediately (`CreateMatch`) so the map is already loaded before they click Queue.
- **Graceful Spin-Down:** When all opted-in Lobby/Queue/Arcade demand drops back to `0` (for example after players pull into Ranked), any currently active 10-minute Arcade round finishes cleanly and returns `#Q1` to `SERVER_STATUS_IDLE`.
- **Deep RL Self-Play Arena (`IdleMode = SelfPlay`):** When explicitly enabled by an operator or Staff member, idle servers train `5v5` `Candidate` vs `Champion` matches (`RlSelfPlayMapPool`) alternating `kongor_rl_cand_team` (`1` vs `2`), collecting 1 Hz `[RL_STEP]` trajectories into a 256 MB / 200-episode ring buffer and updating `games/server/override/bots/rl_runtime/` (mounted inside Docker at `/app/server-rl-runtime`). Candidate promotes automatically only after beating Champion (`WinRate >= 55%` and higher mean return across 20 paired matches).

### Operator Controls

```powershell
# Inspect current Champion/Candidate generation, win rate, and Redis idle mode
kongor rl status

# Toggle Queue Arcade idle mode live (no container restart required)
kongor rl self-play on
kongor rl self-play off

# Run an immediate C# AWAC offline pass from collected JSONL trajectories
kongor rl train --epochs 8

# Export the promoted Champion weights into tracked baseline Lua
kongor rl export

# Roll back Champion to a previous archived generation
kongor rl rollback 2
```

In-game Staff chat commands: `/arcade selfplay on`, `/arcade selfplay off`, `/arcade selfplay status`, and `/arcade status`.

