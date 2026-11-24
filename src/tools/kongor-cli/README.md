<div align="center">

# 🦍 Kongor.Cli (`kongor`)
### *Unified Ecosystem CLI & Native Model Context Protocol (MCP) Server*

[![NuGet](https://img.shields.io/badge/nuget-v5.0.0-0078D4?style=for-the-badge&logo=nuget)](https://www.nuget.org/packages/Kongor.Cli)
[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Global Tool](https://img.shields.io/badge/dotnet%20tool-Global%20CLI-0078D4?style=for-the-badge)](#-getting-started)
[![MCP Ready](https://img.shields.io/badge/MCP%20Server-JSON--RPC%202.0-8E44AD?style=for-the-badge)](#-model-context-protocol-mcp-server)

**Kongor.Cli (`kongor`) is the unified developer and operator CLI router and native Model Context Protocol (MCP) server for ProjectKongor. It provides a single interface for launching games, hosting servers, managing background services, generating AI comics, exporting 3D models, and running diagnostics.**

[⚡ Quick Install](#-quick-install-automated-installer) •
[📖 Command Matrix](#-cli-commands-matrix) •
[🤖 AI MCP Server](#-model-context-protocol-mcp-server) •
[📦 Modding & Assets](#-modding--asset-pipeline-subcommands)

---

### ⚡ Quick Install: Automated Installer (Windows PowerShell)
Run the automated installation script from the repository root to build, package, and register the global `kongor` tool:

```powershell
.\scripts\install-cli.ps1
```
*(Or run zero-install without global registration: `.\kongor.ps1 <command>`)*

---

</div>

## 🚀 Getting Started

### 🌟 1. Automated Global Install (Recommended)
```powershell
.\scripts\install-cli.ps1
```

### ⚡ 2. Zero-Install Execution
```powershell
# Windows PowerShell
.\kongor.ps1 <command>

# Windows Command Prompt
.\kongor.cmd <command>

# Direct dotnet execution
dotnet run --project src/tools/kongor-cli/Kongor.Cli.csproj -- <command>
```

### 🔄 3. Instant Global Self-Update
Once installed globally, update at any time with a single command:
```powershell
kongor refresh
```

---

## 📖 CLI Commands Matrix

### 🎮 1. Core Applications & Microservices
| Command | Aliases | Description |
|---|---|---|
| `kongor play` | `willowmaker`, `launcher` | Launch WILLOWMAKER game client launcher (Vulkan 1.3, 1000 FPS, CDN sync) |
| `kongor host` | `compel`, `server` | Launch COMPEL dedicated server supervisor (1.0ms timer pacer, Win32 sandbox) |
| `kongor gui` | `control-panel`, `nexus`, `admin` | Launch NEXUS desktop administration GUI (Avalonia 11, 3D hero viewer, secrets) |
| `kongor start` | `aspire`, `cluster` | Launch .NET Aspire local cluster (4 core backend services + Garnet/Redis & optional sidecars; `https://localhost:5550`) |
| `kongor docker [action]` | `stack`, `docker-stack` | Turnkey Docker Compose stack: Backend Cluster + Dedicated Match Fleet (`:11334`) + Queue Arcade Fleet (`:11484`) + optional host `hon_x64.exe` client (`up`, `down`, `status`, `logs`, `check`, `init-secrets`; `--perf Eco\|Balanced\|High --client`) |
| `kongor build [target]` | `compile`, `rebuild` | Build projects or categories (`-c Debug\|Release\|All`, `--clean`, `--restore`, `-v`) |
| `kongor master-server` | `master` | Run KONGOR Master Server standalone (SRP-6a authentication, MERRICK multi-DB) |
| `kongor chat-server` | `chat` | Run TRANSMUTANSTEIN TCP Chat Server standalone (zero-copy pipelines, ports 11031-11033) |
| `kongor web-api` | `api` | Run ZORGATH Web Portal REST API backend standalone (ports 5100-5101) |
| `kongor web-ui` | `web` | Run DAWNBRINGER Web Portal MVC UI standalone (ports 5200-5201) |

### 📦 2. Modding, Asset Pipeline & Creative Tools
| Command | Aliases | Description |
|---|---|---|
| `kongor mod <subcmd>` | `mod-cli` | HoN mod package manager (`.honmod`/`.s2z`), DAG conflict resolver |
| `kongor prebake <subcmd>`| `prebake-tool` | Entity XML harvesting & MemoryPack+Zstd binary cache generator |
| `kongor model [cmd]` | `models` | Export HoN heroes/avatars/animations to glTF/GLB (`kongor model export "thriller rhapsody"`) |
| `kongor blender [cmd]` | `launch-blender` | Launch Blender 3D (v5.2+ / v4.2 LTS) or manage lifecycle (`install`, `uninstall`, `stop`) |
| `kongor comfy [cmd]` | `comfyui` | Manage ComfyUI generative AI server on port 8188 (`install`, `uninstall`, `stop`) |
| `kongor replay [cmd]` | `render` | *(Experimental / WIP)* Render replay MP4 or detect teamfight highlights (`kongor replay highlight <file>`) |
| `kongor highlight <file>` | `teamfight` | *(Experimental / WIP)* Automated teamfight highlight clip generator |

### 🔬 3. Developer Diagnostics, Testing & Verification
| Command | Aliases | Description |
|---|---|---|
| `kongor test [target]` | `tunit`, `xunit` | Run test suites across all projects (`--no-build`, `--build`, `--coverage`, `--filter <expr>`) |
| `kongor audit-endpoints` | `audit` | Extract Lua/XML SubmitForm routes & reconcile API endpoints against Master Server |
| `kongor lua-verify` | `lua` | Headless NLua sandbox mocking K2 UI widgets & PHP/Base64 serialization |
| `kongor k2-test` | `k2` | Headless NLua simulation, Cvars, UI Manager, SubmitForm endpoints |
| `kongor benchmark` | `benchmark-mastery` | ClaimMasteryRewardHandler and in-memory EF Core database benchmark |
| `kongor redis-verify` | `redis` | Non-blocking Redis SCAN cursor iteration & purge verification |
| `kongor serialize-verify`| `serialize` | PHP associative array serialization type validation |

### ⚙️ 4. Platform, Secrets & Operations
| Command | Aliases | Description |
|---|---|---|
| `kongor stop [target]` | `kill`, `shutdown` | Stop running services (`dotnet` [default], `docker`, `comfy`, `blender`, `all`) |
| `kongor secret [cmd]` | `secrets`, `config` | Manage centralized User Secrets vault (`init`, `set <k> <v>`, `get <k>`, `list`, `clear`) |
| `kongor logs [service]` | `log`, `tail-logs` | Inspect and live-stream logs from microservices or dedicated servers (`-t`, `-n`) |
| `kongor doctor` | `check` | Run system diagnostics on port availability, .NET runtimes, and dependencies |
| `kongor status` | `info` | Display ecosystem topology, running service ports, process PIDs, and health status |
| `kongor docs` | `docfx`, `doc` | Launch interactive DocFX developer documentation hub on `http://localhost:8080` |
| `kongor ask <question>` | `?`, `ai`, `q` | Natural language AI search across the entire ProjectKongor codebase |

---

## 🌸 Together As One (TAO) — Model Context Protocol (MCP) Server

`Kongor.Cli` embeds **Together As One (TAO)**, a native JSON-RPC 2.0 Model Context Protocol (MCP) server over standard I/O (`stdio`) transport. Named after **Empath's signature ultimate ability**, TAO "hops" directly into the application ecosystem (`hon_x64.exe`, `COMPEL`, and Aspire microservices) to give them a boost, stream real-time vitals, and autonomously self-heal when script or configuration errors occur.

```bash
# Start the TAO MCP stdio server
kongor mcp

# Start with Antigravity tool schema auto-registration
kongor mcp --register-antigravity
```

### 🌟 Key Highlights & Capabilities
* **Specialized MCP Tool Suite**: Comprehensive coverage across functional domains: Client Lifecycle, Dedicated Server Fleet, UI Automation & Offscreen Rendering, VFS & Asset Pipeline, Engine Introspection & Hot-Reload, Diagnostics & Memory Leaks, Database & Replay Analysis, Resilience & Chaos Engineering, Autonomous Self-Healing & Profiling, and Creative AI / Audio / Match Simulation & Vector Search.
* **Autonomous Self-Healing Loop**: Powered by `ClientCommandRemediationService`, TAO catches runtime failures (e.g. `Windows.` nil indexing, `DoEvent('onclick')` signature errors, missing `Script` prefixes, read-only cvars), surgically rewrites the command, and transparently retries with `selfHealed: true`.
* **Zero-Lock Shadow Runtime**: Runs in an isolated `%TEMP%\kongor_mcp_runtime` sandbox so active MCP sessions never lock `.dll` files during `kongor build`.
* **Live Resource Streams**: Subscribe to real-time engine telemetry and logs via `kongor://client/console`, `kongor://client/telemetry`, and `kongor://client/errors`.

👉 **Read the complete guide**: [🌸 Together As One (TAO) Architecture & Tools Guide](../../../docs/tao-mcp/overview.md)

---

## 📦 Modding & Asset Pipeline Subcommands

### Mod Manager
* `kongor mod list`: List all discovered mods with enabled/disabled status and metadata.
* `kongor mod enable <mod>`: Enable one or more mods.
* `kongor mod disable <mod>`: Disable one or more mods (or `all` to disable all active mods).
* `kongor mod inspect <mod>`: Inspect detailed manifest, dependencies, and target files of a mod.
* `kongor mod apply`: Apply enabled mods with DAG ordering to game client (`vfs` or `archive`).
* `kongor mod rollback`: Revert applied modifications cleanly using the rollback journal.

### Prebake Engine Cache
* `kongor prebake all`: Execute full pre-baking pipeline (parse, serialize, lua cache, s2z bundle, stage).
* `kongor prebake entities`: Parse game entity XMLs and serialize to `entity_cache.bin` (MemoryPack+Zstd).
* `kongor prebake lua`: Generate `hero_definitions_cache.lua` for instant client UI resolution.
* `kongor prebake bundle`: Package pre-baked assets into `resources0_prebaked.s2z` archive.
