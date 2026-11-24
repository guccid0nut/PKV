# 🐳 Project KONGOR V — Docker Stack & Container Infrastructure

This directory contains the Docker Compose topologies, multi-stage Linux x86_64 Dockerfiles, and container entrypoint bridges for running Project KONGOR V backend services and COMPEL dedicated game server fleets.

---

## ⚡ Quick Start: Turnkey Full-Stack Launcher (`kongor-stack`)

Launch the entire backend cluster (`garnet`, `master-server`, `chat-server`, `web-api`, `web-ui`), Dedicated Match Server Fleet (`compel-dedicated` on Tier D `:11334`), Queue Arcade Fleet (`compel-queue-arcade` on Tier Q `:11484`), and optional native Windows game client (`hon_x64.exe` / `WILLOWMAKER`) in one command from the repository root:

```powershell
# 1. (First-time setup) Rotate placeholder/dev-default secrets safely (.bak backup created automatically)
.\kongor.ps1 docker init-secrets

# 2. Verify all required secrets, ports, and binaries (fail-closed pre-flight)
.\kongor.ps1 docker check

# 3. Launch full stack (Balanced preset) + native HoN game client on host
.\kongor.ps1 docker up --perf Balanced --client

# 4. Inspect unified container + COMPEL fleet status or follow logs
.\kongor.ps1 status
.\kongor.ps1 docker logs -s master-server -f

# 5. Tear down the Docker stack when finished
.\kongor.ps1 stop docker
```

> [!TIP]
> **Desktop GUI Control:** You can also launch, switch presets (`Eco` / `Balanced` / `High`), tail `logs/docker/<service>/` live, and rotate placeholder secrets directly from **`NEXUS.ControlPanel.GUI`** (`.\kongor.ps1 gui`) via the **`🐳 Docker Stack ▾`** toolbar menu.

---

## 📂 Directory Manifest

| File | Purpose |
| :--- | :--- |
| [`docker-compose.stack.yml`](docker-compose.stack.yml) | Full turnkey `kongor-stack` topology: `garnet` (`:28298`), optional `postgres` (`--profile local-db`, `:25432`), `master-server` (`:55555`), `chat-server` (`:5554` HTTP / `:11031-11033` TCP), `web-api` (`:5200`), `web-ui` (`:5266`), `compel-dedicated` (`:11334`, UDP `11335-11342`), and `compel-queue-arcade` (`:11484`, UDP `11485-11492`). |
| [`docker-compose.community.yml`](docker-compose.community.yml) | Standalone volunteer community host Compose file for running `compel-dedicated` against a remote Master Server. |
| [`Dockerfile.service`](Dockerfile.service) | Slim `aspnet:11.0` runtime image packaging pre-published Linux x64 backend services (`master-server`, `chat-server`, `web-api`, `web-ui`). |
| [`Dockerfile.dedicated`](Dockerfile.dedicated) | Linux x86_64 dedicated server runtime packaging `COMPEL`, `libk2_native_guard.so` (`LD_PRELOAD` crash & ABI guard), `curl`, and `socat` loopback bridges. |
| [`entrypoint.sh`](entrypoint.sh) | Pure-LF POSIX entrypoint for COMPEL containers: configures `LD_LIBRARY_PATH` / `LD_PRELOAD`, starts `socat` loopback bridges (`127.0.0.1:11032-11033` $\to$ `chat-server` and `127.0.0.1:55555` $\to$ `master-server` for `.honreplay` uploads), and execs `COMPEL`. |
| [`docker-compose.garnet.yml`](docker-compose.garnet.yml) / [`garnet.conf`](garnet.conf) | Standalone Microsoft Garnet RESP3 cache configuration (`:28298`). |
| [`docker-compose.infra.yml`](docker-compose.infra.yml) / [`docker-compose.pyroscope.yml`](docker-compose.pyroscope.yml) | Optional observability & continuous profiling sidecars (`Pyroscope :4040`). |
| [`run_docker_servers.ps1`](run_docker_servers.ps1) | Standalone helper for launching an isolated `compel-dedicated-server` container against an external or host Master Server. |

---

## ⚙️ Performance Presets (`--perf`)

Orchestrated by [`../../scripts/docker/launch_docker_stack.ps1`](../../scripts/docker/launch_docker_stack.ps1):

| Preset | Aliases | Dedicated Workers (`:11335+`) | Queue Arcade Workers (`:11485+`) | `.NET GC` Mode | `COMPEL_PRIORITY_MODE` | Target Hardware |
| :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **`Eco`** | `Low`, `Minimal` | `1` (`1` warm) | `1` (`1` warm) | Workstation (`Conserve=7`) | `Background` | Laptops / background dev (`~6 GB` cap) |
| **`Balanced`** *(default)* | `Medium`, `Default` | `2` (`1` warm) | `2` (`1` warm) | Workstation (`Conserve=3`) | `Adaptive` | 16–32 GB dev/gaming PCs |
| **`High`** | `Max`, `Ultra`, `Tournament` | `4` (`2` warm) | `2` (`2` warm) | Server GC | `HighPriority` | 32–64 GB+ workstations & LAN hosts |

*(Override worker counts anytime with `--match-instances 1..8` and `--arcade-instances 1..8`.)*

---

## 🧠 Key Architectural Details

1. **Fail-Closed Secret Pre-Flight & `.bak` Rotation**:
   - `launch_docker_stack.ps1` inspects the canonical User Secrets vault (`00005072-6f6a-6563-7420-4b4f4e474f52`), `.env`, and environment variables before starting any container.
   - Missing or placeholder secrets exit immediately (`exit 2`) with exact remediation commands; `init-secrets` backs up `secrets.json` to `secrets.json.<timestamp>.bak` before generating cryptographic keys and synchronizing alias keys across services.
2. **Shared Linux `ext4` Server Volume (`kongor-server-binaries`)**:
   - `games/server` is seeded once into a named Linux `ext4` Docker volume (`kongor-server-binaries`) mounted read-only at `/app/server` by both `compel-dedicated` and `compel-queue-arcade`, avoiding slow Windows NTFS `/drvfs` 9P bind-mount I/O and duplicate multi-GB staging.
3. **Split-Horizon LAN UDP Routing (`CHAT_SERVER_LAN_HOST`)**:
   - `launch_docker_stack.ps1` resolves the host machine's routed LAN IPv4 address into `userdata/docker/.env.stack` (`CHAT_SERVER_LAN_HOST`), allowing native Windows `hon_x64.exe` clients to connect to containerized match/arcade UDP ports (`11335+` / `11485+`) without hairpin NAT failures while sibling containers communicate over internal Docker DNS.
4. **Container Loopback Bridges (`entrypoint.sh`)**:
   - Inside COMPEL containers, `socat` bridges `127.0.0.1:11032-11033` to `chat-server` and `127.0.0.1:55555` to `master-server` so native K2 `hon-x86_64-server` manager sockets and `Configuration.CDN.Host` `.honreplay` uploads work transparently without binary patching.

📖 **Further Reading:**
- [Container & Kubernetes Hosting Guide](../../docs/infrastructure/container-hosting-guide.md)
- [Network Ports & IPC Matrix](../../docs/infrastructure/network-and-ports.md)
