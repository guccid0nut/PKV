# 🚀 What's New in Project Kongor V (v5.0, v5.0.1 & v5.0.2)

<div align="center">

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia 11](https://img.shields.io/badge/Avalonia-11.2-8E44AD?style=for-the-badge&logo=avalonia&logoColor=white)](https://avaloniaui.net/)
[![Aspire 13.5](https://img.shields.io/badge/Aspire-13.5-0078D4?style=for-the-badge&logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![Vulkan 1.3](https://img.shields.io/badge/Vulkan-1.3-E74C3C?style=for-the-badge&logo=vulkan&logoColor=white)](https://www.vulkan.org/)
[![Snapdragon & ARM64](https://img.shields.io/badge/ARM64-Snapdragon%20X%20Elite-0052CC?style=for-the-badge&logo=qualcomm&logoColor=white)](#)

**The Great Unification: One Ecosystem, Cross-Platform Services, Repaired Classic & Custom Maps.**

> 📖 Looking for player-facing patch highlights (**War Effort Quests**, **High-Hz DirectFlip, Implicit Vulkan Layer Shield & VRR Readiness**, **Offline/Online Shop Guides**, **Offline/P2P Select-a-Mode & Store Guards**, **10v10 Bot Scaling & Combat-Bounded Asset Prewarming**, **Matchmaking & Clan Fixes**, **Queue Arcade Bot/Player Slot Swaps**, **Custom Map Stats & Scoreboards**, **Early 20-Map Bot Support**, and **1-Command Docker Self-Hosting**)? Read the [**5.0, 5.0.1 & 5.0.2 Update Notes**](v5-update-notes.md).

</div>

---

## 🏛️ 1. The Great Consolidation: Everything in One Ecosystem

**Project Kongor V** consolidates the entire Heroes of Newerth preservation stack into a single, unified, high-performance monorepo:

* **🦍 Kongor**: Our unified CLI (`Kongor.Cli`) and Core protocol / modding framework (`Kongor.Core`, `Kongor.Modding`).
* **🏛️ NEXUS**: The central Master Server backend (`NEXUS.MasterServer`), shared contracts (`NEXUS.Contracts`), and desktop administration GUI (`NEXUS.ControlPanel.GUI`).
* **🛡️ COMPEL**: The dedicated match server fleet supervisor with 1.0ms timer pacer and Win32 sandboxing.
* **🚀 WILLOWMAKER**: The desktop game client launcher with high-FPS engine patching and DXVK Vulkan 1.3 translation.
* **☁️ Coordinated Services Suite**: A microservices backend (`src/services/`) orchestrated via .NET Aspire (`aspire-host`, `master-server`, `chat-server`, `web-api`, `web-ui`).
* **🛠️ Comprehensive Developer Tools Suite**: Built-in engineering, testing, 3D modding, and AI tools (`src/tools/`).

### 🏛️ Core Architecture & Subsystems

| Subsystem | Scope / Directory | Primary Role & Capabilities |
|---|---|---|
| **🦍 Kongor CLI** | `src/tools/kongor-cli/`, `src/tools/kongor-shim/` | Unified CLI (`kongor play`, `host`, `gui`, `start`, `where`, `workspace`, `gate`, `mod`, `doctor`, `refresh`) with `~/.kongor/bin` shim |
| **⚡ Kongor Core** | `src/libs/protocol-core/`, `src/libs/modding/` | Protocol engine, zero-copy serialization, and `.honmod` package manager |
| **🏛️ NEXUS Master Server** | `src/services/master-server/` | Central authentication, SRP-6a security, MERRICK multi-DB (Postgres/Supabase/MSSQL), store, stats ingestion |
| **🛡️ COMPEL Fleet Manager** | `src/apps/compel/` | Dedicated match server supervisor with 1.0ms timer pacer, Win32 Job Object sandbox, UDP rate limiting |
| **🚀 WILLOWMAKER Launcher** | `src/apps/willowmaker/` | Client launcher, high-FPS dynamic engine patcher, DXVK Vulkan 1.3 translation, Discord Rich Presence, CDN sync |
| **🖥️ NEXUS Control Panel** | `src/apps/control-panel/` | Avalonia 11 desktop administration GUI, interactive Three.js 3D hero viewer, secrets vault, live log streamer |
| **☁️ Backend Services Suite** | `src/services/` | Coordinated microservices: Aspire Host (`5550`), Chat Server (TRANSMUTANSTEIN `11031`), Web API (ZORGATH `5200`), Web UI (DAWNBRINGER `5266`) |
| **🛠️ Developer Tools Suite** | `src/tools/` | Integrated tools: Blender 3D addon, glTF model exporter, ComfyUI generative AI bridge, NLua verifier, K2 test sandbox |

### 🦍 Key Architectural Principles

- **Single Source of Truth**: Shared contracts (`NEXUS.Contracts`), core protocols (`Kongor.Core`), and service defaults (`NEXUS.ServiceDefaults`) are maintained in one place.
- **Atomic Schema Evolution**: Database migrations in `MERRICK.DatabaseContext` synchronize across Master Server, Web API, Web UI, and the Control Panel GUI.
- **Unified Build & Packaging**: Build, pack, and verify all backend services, desktop apps, and developer tools with a single command (`kongor build`).

---

## 🌍 2. Cross-Platform Services & Hardware Support (x86_64, Apple Silicon & Snapdragon ARM64)

Project Kongor V's backend microservices, CLI tools, and Avalonia 11 desktop Control Panel are built on **.NET 11** to run natively across major operating systems and CPU architectures (while the K2 engine game client `hon_x64.exe` runs natively on Windows x64, via Windows ARM64 Prism translation, or via Wine/Proton/CrossOver on Linux/macOS):

```
+---------------------------------------------------------------------------------------+
|                       PLATFORM & HARDWARE COMPATIBILITY MATRIX                        |
+---------------------------------------------------------------------------------------+
|                                                                                       |
|   🖥️ WINDOWS (x64 & ARM64)       🐧 LINUX (x86_64 & aarch64)      🍎 macOS (Apple Silicon) |
|   - Windows 10 & 11 (x64)        - Ubuntu 22.04 / 24.04 LTS       - macOS Sonoma & Sequoia|
|   - Qualcomm Snapdragon X Elite  - Debian 12 / Arch Linux         - Apple M1, M2, M3, M4  |
|   - Windows Server 2019/2022/2025- Headless Docker Containers     - Native .NET / Avalonia|
|                                                                                       |
+---------------------------------------------------------------------------------------+
```

### ⚡ Qualcomm Snapdragon X Elite & Windows on ARM64
Project Kongor V supports **Windows on ARM** PCs powered by Qualcomm Snapdragon X Elite and Snapdragon X Plus processors:
- **Native ARM64 Services & GUI**: All .NET 11 microservices, the Control Panel GUI, and CLI tools compile directly to ARM64 instructions.
- **ReadyToRun & Dynamic PGO**: Compiled with Release `PublishReadyToRun=true`, `TieredPGO=true`, and `<IsAotCompatible>true</IsAotCompatible>` source-generated serialization for fast startup and low memory overhead.
- **Heterogeneous Core Scheduling**: Handles hybrid CPU architectures (performance/efficiency core topologies).

---

## 🛠️ 3. The All-in-One Subsystem Suite

Project Kongor V includes a suite of built-in tools, engines, and services:

### 1. 🦍 Unified Kongor CLI (`kongor`)
A global command-line tool whose commands resolve a registered source checkout or playtest bundle from outside the project folder (`~/.kongor/config.json` + `~/.kongor/bin/kongor` forwarder shim):
- `kongor play`: Launches WILLOWMAKER with unlocked FPS and DXVK Vulkan 1.3 translation.
- `kongor host`: Launches COMPEL dedicated server supervisor with 1.0ms timer precision.
- `kongor gui`: Launches the cross-platform Avalonia 11 administration dashboard.
- `kongor start`: Launches the local .NET Aspire backend cluster (4 core services + Garnet/Redis & optional sidecars).
- `kongor where` / `kongor workspace`: Shows the active workspace root and switches between registered checkouts or playtest bundles.
- `kongor gate` / `kongor test`: Runs the 6-tier quality gate and lock-serialized test suites.
- `kongor mod`: `.honmod` package manager (install, toggle, verify, harvest).
- `kongor prebake`: Entity XML harvester and MemoryPack+Zstd binary cache compiler.
- `kongor highlight`: *(Experimental / WIP)* Auto-detects and renders teamfight clips to MP4.
- `kongor doctor`: System diagnostics for ports, runtimes, PATH shadowing, and dependencies.
- `kongor completion <shell>`: Emits static tab-completion scripts (`powershell`, `bash`, `zsh`, `fish`).
- `kongor refresh`: Side-by-side CLI update and reinstallation without locking running binaries.

### 2. 🤖 Model Context Protocol (MCP) Server for AI Coding Agents (`101` Tools)
Project Kongor V features a **Model Context Protocol (MCP)** server (**Together As One / TAO**) built natively in C# with zero-lock shadow runtime execution:
- Exposes **101 structured `kongor_*` agent tools**: `kongor_vfs_locate`, `kongor_vfs_resolve`, `kongor_hot_reload`, `kongor_client_eval_lua`, `kongor_capture_window`, `kongor_ui_visual_diff`, `kongor_match_launch`, `kongor_game_lua`, `kongor_game_command`, `kongor_replay_playback`, `kongor_ui_control`, and `kongor_diagnostics`.
- Features **Zero-Focus-Theft Mailbox IPC (`CompositeGameIpcBridge`)** (`NexusMailboxIpcBridge` + `NativeClientIpcBridge` via `version.dll` NativeGuard) and sub-millisecond `.s2z` ZIP Central Directory indexing (`VfsIndexService`).

### 3. 🛡️ COMPEL Dedicated Server Fleet Manager
- **Direct Dedicated Worker Supervision (`hon_x64 -dedicated`)**: Replaces the legacy `-manager` child orchestration model, granting COMPEL ownership of child process standard input (`stdin`) for direct console command execution.
- **Dual-Channel Dedicated Command Routing**: Supports direct synchronous writes to worker `stdin` (primary) and per-port Named Pipe IPC (`\\.\pipe\hon-server-{port}`) adhering to the Dedicated Game Server Command Routing Rule.
- **1.0ms Timer Pacer**: Enforces sub-millisecond Windows timer resolution (`timeBeginPeriod(1)`) to reduce server tick jitter across K2's 20 Hz (`svr_gameFPS 20`) authoritative simulation ticks.
- **High-Frequency Packet Rate Ceiling**: Configures `net_maxPacketRate 128`, `cl_packetEpsilon 0.001`, and `sys_sleepTime 0` on every dedicated worker, with console buffer smoothing to reduce disk I/O jitter.
- **Granular Crash Recovery & Isolated Restarts**: Independent per-worker lifecycle management; instance crashes restart only the affected port with exponential backoff while sibling matches continue running (`POST /instances/{port}/restart`).
- **Win32 Job Object Sandboxing**: Enforces memory quotas and cleans up child `hon_x64.exe` processes on shutdown or crashes.
- **Token-Bucket UDP Rate Limiting**: Lock-free rate limiter using 64-bit atomic CAS protecting server ports (`20000`-`29999`) against UDP packet floods.
- **UDP Outbox Serialization & Buffer Clamping**: Serialized channel datagram transmission prevents multi-threaded `SendToAsync` race conditions, while loopback socket buffer clamping (64 KB) reduces unpaged kernel memory overhead.
- **P-Core Affinity Pinning**: Auto-detects Intel 12th–15th Gen and AMD Zen4c/Zen5c hybrid cores to bind match slaves to high-performance cores.

### 4. 🖥️ NEXUS Desktop Control Panel GUI
- Built with **Avalonia 11.2** and ReactiveUI for Windows, Linux, and macOS.
- **Launcher Sync & Server Chat Console**: Named Pipe IPC (`CockpitAppBridgeService`) syncs Local, LAN, Tailscale, or WAN Master/CDN addresses to an open `WILLOWMAKER` window, and `GameServerChatConsoleControl` streams player chat and commands for `COMPEL` match workers.
- **Interactive Three.js 3D Hero Viewport**: WebView2/WebGL viewport to view 3D heroes, skins, animations, and particle effects.
- **Native Secrets Vault Editor**: Graphical editor for developer secrets (`00005072-6f6a-6563-7420-4b4f4e474f52`) without committing credentials to Git.
- **Real-Time Live Log Streamer**: Tail and filter logs across Master Server, Chat Server, and dedicated server instances with syntax highlighting.

### 5. 🎬 Headless Replay-to-MP4 Video Renderer *(Experimental / WIP)*
- Captures Vulkan 1.3 presentation (`vkQueuePresentKHR`) via custom layer `VkLayer_hon_replay_capture.dll`.
- Offscreen frame buffer capture with WASAPI 48kHz stereo loopback audio synchronization.
- Hardware-accelerated **NVENC H.264 / HEVC** video encoding producing 60FPS MP4 files.
- Experimental teamfight detection analyzing combat log spikes, multi-kills, and objective steals.

### 6. 🎨 Creative & 3D Modding Pipelines *(WIP / Creator Toolkits)*
- **Blender 4.2+ Native Addon**: Import and export of HoN `.model`, `.mdf`, `.clip`, and `.s2z` files, with conversion to glTF 2.0 / GLB.
- **ComfyUI Generative AI Bridge *(Experimental)***: Optional local bridge on port 8188 for concept art and creative workflows.
- **Mod Package Manager (`Kongor.Modding`)**: Validates `.honmod` manifests, unpacks resources, resolves file collisions, and applies dynamic XML patches.

### 7. ☁️ Modern Cloud-Native Backend (.NET Aspire 13.5 & MERRICK)
- **.NET Aspire Orchestration**: Launch the 4 core backend microservices (`master-server`, `chat-server`, `web-portal-api`, `web-portal-ui`), Garnet/Redis cache container, and optional sidecars with one command (`aspire run` or `kongor start`), complete with an interactive telemetry dashboard on `https://localhost:5550`.
- **MERRICK Multi-Database Context**: Supports **PostgreSQL 17**, **Supabase**, and **Microsoft SQL Server 2022**.
- **Decomposed 4-Stage Match Processing Pipeline**: Distinct stages for validation/account resolution, economy currency rewards, Bayesian OpenSkill rating computation, and database persistence with explicit EF Core transaction boundaries (`BeginTransactionAsync(IsolationLevel.ReadCommitted)` + `IExecutionStrategy`).
- **Post-Commit Side Effect Finalization**: Decouples Redis cache invalidation, EventBus `MatchCompletedEvent` dispatch, and cloud replay auto-upload queueing so side-effect failures do not roll back committed matches.
- **Match Dead-Letter Queue & Forensics**: Automatically quarantines faulted match jobs, writes forensic JSON audit dumps to disk (`logs/dead_letter_matches/`), and supports programmatic replay.
- **Resilient Protocol Routing**: Server Requester returns safe PHP serialized boolean false (`b:0;`) with HTTP 200 OK on unsupported query actions instead of crashing.
- **Unified S2 Cryptography (`S2Crypto`)**: Centralized SRP-6a password hashing, salt generation, and chat server authentication cookies.
- **RFC 8878 Zstandard Compression (`ZstdDeltaCompressor`)**: Pooled recyclable memory streams and span-based APIs for COMPEL UDP proxy relays and managed telemetry streams.

---

## 📊 4. Project Kongor V Subsystem & Capability Matrix

| Capability / Subsystem | Architecture & Technical Implementation | Key Highlights |
|---|---|---|
| **Repository Architecture** | Unified Monorepo (`ProjectKongor`) | All services, desktop apps, tools, and libraries compile and test together |
| **CLI & Protocol Core** | `Kongor.Cli`, `Kongor.Shim` & `Kongor.Core` | Workspace-resolving CLI, `~/.kongor/bin` shim, protocol core & `.honmod` manager |
| **Master Server Architecture** | `NEXUS` Master Server | Multi-DB (PostgreSQL, Supabase, SQL Server via MERRICK), SRP-6a, Polly v8 resilience |
| **Backend Services Suite** | Coordinated Microservices | Aspire Host (`5550`), TRANSMUTANSTEIN Chat (`11031`), ZORGATH API (`5200`), DAWNBRINGER UI (`5266`) |
| **Developer Tools Suite** | 12+ Integrated Tools | Native Blender 4.2+ addon, glTF model exporter, ComfyUI bridge, NLua verifier, K2 test harness |
| **Runtime & Target Framework** | .NET 11.0 (`net11.0`) & C# 14 | ReadyToRun (`PublishReadyToRun`), Dynamic PGO (`TieredPGO`), `<IsAotCompatible>` static analysis |
| **Operating Systems** | Windows, Linux & macOS | Native cross-platform backend & GUI; Windows x64 K2 game client (Wine/Proton on Linux/macOS) |
| **Hardware & Architectures** | x86_64, Apple Silicon (M1-M4), ARM64 | Native ARM64 compilation for .NET services & GUI on Snapdragon X Elite & Apple Silicon |
| **CLI Installation** | Side-by-side installer (`install-cli.ps1`) | `~/.kongor/bin/kongor` forwarder shim, `~/.kongor/config.json` workspaces, shell completions & `kongor refresh` |
| **AI Agent Integration** | Native C# Model Context Protocol (MCP) Server | 101 agent tools across 10 functional domains with Zero-Focus-Theft Mailbox IPC & `VfsIndexService` |
| **Desktop Admin GUI** | Avalonia 11.2 & ReactiveUI (NEXUS Control Panel) | `WILLOWMAKER` endpoint sync, dedicated server chat console, Three.js 3D viewport & secrets vault |
| **Client Engine Pacing** | Patched K2 Engine with Unlocked FPS, DirectFlip & DXVK Vulkan 1.3 | Uncapped frame pacing (up to 1000 FPS), Pure DirectFlip Vulkan layer gating, DXVK low-latency pacing, cross-PC EDID/OS VRR readiness detection & 1-click sub-refresh FPS cap |
| **Dedicated Server Manager** | COMPEL Server Supervisor | 1.0ms timer pacer (`timeBeginPeriod(1)`), Win32 sandbox, UDP rate limiting, P-core affinity |
| **Database Engines** | MERRICK Multi-Database Context | Native support for PostgreSQL 16+, Supabase, and Microsoft SQL Server 2022 |
| **Caching Layer** | HybridCache (L1 Memory + L2 Redis / Garnet) | High-speed memory caching + distributed Redis/Garnet with MemoryPack and Zstd compression |
| **Replay Video Renderer** | Vulkan Headless Capture *(WIP)* | Offscreen frame capture, WASAPI 48kHz audio sync, NVENC MP4 encoding |
| **Creative & 3D Pipelines** | Blender 4.2+ Native Addon & Model Exporter | Direct import/export of HoN `.model`, `.mdf`, `.clip` with batch glTF/GLB conversion |
| **Cloud Orchestration** | .NET Aspire 13.5 AppHost | Single-command local cluster orchestration with real-time dashboard on port `5550` |
| **Observability & Diagnostics** | OpenTelemetry, Prometheus & Pyroscope | Structured distributed tracing, live metrics, and CPU profiling flamegraphs |

---

## ⚡ 5. V5.1 Core Architecture & Systems Reliability Milestone

Recent architectural updates across core domains include:

### 🔐 Unified Cryptographic Foundation (`Kongor.Core.Security.S2Crypto`)
* **Single Authoritative API**: Consolidates legacy Heroes of Newerth SRP-6a hashing into `S2Crypto`, removing duplicate implementations across Web API, Web UI, and Master Server.
* **Parameter Inversion Guard**: Distinguishes between plaintext password hashing (`ComputeSrpFromPlaintext`) and pre-hashed MD5 client credentials (`ComputeSrpFromPrehashedMd5`), preventing double-hashing authentication issues.
* **Deterministic Tokens**: Provides unified implementations for chat server cookie authentication and match server tokens.

### 🚀 Zero-Allocation Ingress Engine (`System.IO.Pipelines`)
* **Streaming Socket Framing**: `TRANSMUTANSTEIN.ChatServer` (`ChatSession.cs`) now ingests raw TCP byte streams directly through `System.IO.Pipelines.PipeReader`.
* **Pooled Buffering**: Header lengths are decoded directly from `ReadOnlySequence<byte>` using `BinaryPrimitives.ReadUInt16LittleEndian`, and packet segments are dispatched via `ArrayPool<byte>.Shared`, reducing per-packet heap allocations.
* **Asynchronous Redis Subscriptions**: `PlayerStatusHydrationSubscriber` refactored to pure `async/await`, avoiding sync-over-async thread blocking during match spikes.

### 🏛️ Decomposed Match Pipeline & Atomic Transactions
* **Modular Match Services**: Decomposed the monolithic `MatchProcessingService` into four focused domain services:
  1. `MatchIngestionHandler`: Payload validation, participant normalization, and account resolution.
  2. `HeroMasteryProgressionService`: Hero mastery XP calculation, rewards, and difficulty voting.
  3. `OpenSkillRatingCalculator`: Bayesian $\mu$ and $\sigma$ rating updates.
  4. `ReplayPersistenceWorker`: Database entity commit and post-commit cloud replay uploads.
* **Atomic Transaction Isolation**: Wrapped database mutations inside an `IExecutionStrategy` retry-aware `IDbContextTransaction`. Guest accounts are batched into a single commit, reducing in-loop database roundtrips. Post-commit side effects (S3 uploads, Redis pub/sub) trigger after database commit.

### 🛡️ High-Throughput UDP Datagram Channelization
* **COMPEL Socket Synchronization**: Outbound server-to-client datagrams in `UDPForwarder` are now channelized through an internal `Channel<OutboxDatagram>` with a dedicated writer pump, avoiding concurrent socket write conflicts under heavy match traffic.

### 🌐 Zero-Copy UTF-8 Multi-Byte PHP Deserialization
* **Byte-Length Precision**: `PhpDeserializer.cs` now accounts for UTF-8 byte lengths versus UTF-16 code units, properly handling international text (Cyrillic, CJK) and 4-byte surrogate emoji characters.

### 📦 CLI Decoupling & Spectre.Console Modernization
* **Package Footprint Reduced by 85%**: Decoupled Avalonia desktop GUI dependencies into a standalone tool (`src/tools/headless-renderer/`), slimming the `kongor` CLI package from ~180 MB down to ~25 MB.
* **Modern Command Architecture**: Migrated from legacy `System.CommandLine` beta to `Spectre.Console.Cli`, breaking down `Program.cs` into clean, modular command classes with rich terminal formatting.

---

## 🚀 6. Getting Started with Project Kongor V

Ready to explore? Get up and running in under two minutes:

```powershell
# 1. Install the unified CLI globally
.\scripts\install-cli.ps1

# 2. Check ecosystem health and available tools
kongor doctor
kongor status

# 3. Launch the full local microservice cluster
kongor start

# 4. Or launch the desktop administration GUI
kongor gui
```

For complete documentation, visit the **[Project Kongor Developer Hub](../index.md)** or run `kongor docs` to launch the interactive local DocFX documentation site on [http://localhost:8080](http://localhost:8080).

