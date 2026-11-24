<div align="center">

# 🦍 Project KONGOR V (`PKV`)
### *Unified Platform Architecture & Modern Runtime for Heroes of Newerth*

[![Build & Test](https://github.com/guccid0nut/PKV/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/guccid0nut/PKV/actions/workflows/build-and-test.yml)
[![MCP E2E Smoke](https://github.com/guccid0nut/PKV/actions/workflows/e2e-smoke.yml/badge.svg)](https://github.com/guccid0nut/PKV/actions/workflows/e2e-smoke.yml)
[![Quality Gates](https://github.com/guccid0nut/PKV/actions/workflows/quality-gates.yml/badge.svg)](https://github.com/guccid0nut/PKV/actions/workflows/quality-gates.yml)
[![Docs & REUSE](https://github.com/guccid0nut/PKV/actions/workflows/docs-and-reuse.yml/badge.svg)](https://github.com/guccid0nut/PKV/actions/workflows/docs-and-reuse.yml)

[![Version: 5.0.2](https://img.shields.io/badge/Version-5.0.2-2ECC71?style=for-the-badge)](CHANGELOG.md)
[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia 11.2.3](https://img.shields.io/badge/Avalonia-11.2.3-8E44AD?style=for-the-badge&logo=avalonia&logoColor=white)](https://avaloniaui.net/)
[![Aspire 13.6.5](https://img.shields.io/badge/Aspire-13.6.5-0078D4?style=for-the-badge&logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![Vulkan 1.3](https://img.shields.io/badge/Vulkan-1.3-E74C3C?style=for-the-badge&logo=vulkan&logoColor=white)](https://www.vulkan.org/)
[![Snapdragon & ARM64](https://img.shields.io/badge/ARM64-Snapdragon%20X%20Elite-0052CC?style=for-the-badge&logo=qualcomm&logoColor=white)](#)
[![REUSE Compliant](https://img.shields.io/badge/REUSE-AGPL--3.0%20%7C%20MPL--2.0%20%7C%20CC--BY--NC--SA--4.0-2ECC71?style=for-the-badge)](REUSE.toml)

**The unified, cleanroom open-source master server, dedicated server supervisor, modern client launcher, native Vulkan/audio/stability layer, and developer tooling ecosystem for Heroes of Newerth (HoN).**

[🚀 Quick Start](#-two-track-quick-start) •
[🎯 Decision Funnel](#-what-are-you-looking-to-do-the-decision-funnel) •
[📸 Visual Showcase](#-visual-showcase--screenshots) •
[🗺️ Architecture](#-ecosystem-architecture--data-flows) •
[📖 Changelog](CHANGELOG.md) •
[📚 Docs Hub](docs/index.md) •
[🌸 TAO MCP](docs/tao-mcp/overview.md) •
[📜 Charter](docs/charter.md) •
[🙏 Credits](docs/credits-and-gratitude.md) •
[🤖 AI Notes](docs/ai-gratitude-and-methodology.md)

</div>

---

> [!IMPORTANT]
> ### ⚖️ Video Game Preservation, Provenance & Non-Affiliation Notice
> **Project KONGOR V (`PKV`) is an independent, non-profit video game preservation and interoperability initiative maintained by `guccidonut`.**
> - **Cleanroom Source Repository (`0` Git LFS):** This public repository contains **zero** retail *Heroes of Newerth* executables, `.s2z` archives, 3D models, textures, icons, or audio files. Every C# service, launcher, native C++ shim (`native/`), and unit test builds and runs out of the box from pure source code without game files present.
> - **Foundational Upstream Attribution:** Project KONGOR V gratefully builds upon and extends foundational `AGPL-3.0` open-source work by **`[K]ONGOR` ([Project-KONGOR-Open-Source](https://github.com/Project-KONGOR-Open-Source))** alongside newly authored PKV platform, native Vulkan/audio/guard (`MPL-2.0`), and TAO MCP (`AGPL-3.0-or-later`) tooling. PKV is an independent continuation and is **not** affiliated with or endorsed by `[K]ONGOR`, the original Project KONGOR staff, or **Kongor Studios (`HoN Reborn`)**. See [`NOTICE`](NOTICE), [`NAMING_POLICY.md`](NAMING_POLICY.md), and [`INTEROPERABILITY_AND_DMCA.md`](INTEROPERABILITY_AND_DMCA.md).
> - **Intellectual Property:** *Heroes of Newerth*™, *Newerth*™, and all official retail assets, characters, audio, and lore are the intellectual property and registered trademarks of **Garena Online Private Limited** (and historically **S2 Games** and **Frostburn Studios**). This project is not affiliated with, endorsed by, or sponsored by Garena, S2 Games, or Frostburn Studios.
> - **The Social Contract:** Project KONGOR V will **always and forever remain 100% free and open-source**—zero donations, zero paywalls, zero monetization. Read our full [Preservation Charter](docs/charter.md).

> [!TIP]
> ### 🚀 What's New in Project KONGOR V (`v5.0.2`)
> **War Effort Daily Quests *(Initial Rollout)*, Valkey Matchmaking & Clan Tag Fixes, Queue Arcade Bot/Player Slot Swaps, Custom Map Stats & Lobby Checks, Global `kongor` CLI Workspace Support, and Avatar/Mode Fixes:** Building on v5.0.1's Queue Arcade, Friendslist Broadcasting, and Early 20-Map Bot Support across Windows, Linux, macOS, and ARM64.
>
> 📖 Read the [**5.0–5.0.2 Update Notes**](docs/releases/v5-update-notes.md), [**What's New in 5.0**](docs/releases/whats-new-in-v5.md), and [**CHANGELOG.md**](CHANGELOG.md).

---

## <a id="-two-track-quick-start"></a>🚀 Two-Track Quick Start

Project KONGOR V separates **cleanroom platform engineering** from **local game runtime execution** so contributors can build and test everything immediately with zero proprietary files.

### 🛠️ Track A: Developers & Self-Hosters (Cleanroom — Zero Retail Files Required)

Clone `--recurse-submodules` (fetches only `MIT`-licensed `RmlUi` and `libdeflate`), build .NET + native C++, and run the entire unit test suite or turnkey Docker backend with zero `.s2z` or game binaries:

```powershell
# 1. Clone cleanroom repository & pinned .NET tools (global.json: .NET 11.0.100)
git clone --recurse-submodules https://github.com/guccid0nut/PKV.git
cd PKV
dotnet tool restore

# 2. Build .NET solution + native C++/Vulkan layer and run fast unit tests
.\kongor.ps1 build
.\kongor.ps1 build native
.\kongor.ps1 test unit

# 3. (Optional) Register global `kongor` CLI + initialize local secrets safely
.\scripts\install-cli.ps1
.\kongor.ps1 docker init-secrets

# 4. Launch 1-command Docker stack, local .NET Aspire cluster (:5550), or Admin GUI
.\kongor.ps1 docker up --perf Balanced     # 7-container backend + match & queue arcade fleets
.\kongor.ps1 aspire                        # Local .NET Aspire orchestrator + OpenTelemetry
.\kongor.ps1 gui                           # NEXUS Control Panel Avalonia 11.2.3 desktop GUI
```

> 🤖 **Using an AI Coding Agent?** See **[Optional Agent & Asset Tooling Setup](docs/getting-started/optional-agent-tools.md)** or run `.\scripts\Install-OptionalAgentTools.ps1` to configure optional 3D, audio, proxy, and search MCP integrations.

### 🎮 Track B: Players (BYOA `WILLOWMAKER` Setup — Local HoN `v4.10.1`)

To play locally or host live dedicated match servers, supply your own lawfully acquired *Heroes of Newerth (`v4.10.1`)* installation:

```powershell
# Option 1: Hydrate/symlink your existing HoN 4.10.1 folder into games/client & games/server
.\scripts\vfs\Hydrate-LocalGameAssets.ps1 -GameDir "C:\Games\Heroes of Newerth 4.10.1"

# Option 2: Or configure GameClientPath / GameServerPath in NEXUS Control Panel GUI -> Secrets & Config
.\kongor.ps1 gui

# Launch WILLOWMAKER (1000 FPS unlock, DXVK Vulkan 1.3, PKV Guard & CDN sync)
.\kongor.ps1 play

# Or host dedicated match servers with COMPEL (1.0ms timer pacer & Win32 Job Object isolation)
.\kongor.ps1 host
```

<details>
<summary>📋 <strong>Unified Kongor CLI Command Matrix (Click to Expand)</strong></summary>

Once installed via `.\scripts\install-cli.ps1` (or `tools-<rid>/kongor install` in playtest bundles), `kongor` runs from **any directory** on Windows, Linux, and macOS via `~/.kongor/bin/kongor` and `~/.kongor/config.json` (override anytime with `--root <path>` or `KONGOR_ROOT`). Zero-install execution also works via `.\kongor.ps1 <command>`, `.\kongor.cmd <command>`, or `dotnet run --project src/tools/kongor-cli/Kongor.Cli.csproj -- <command>` (update global install with `kongor refresh`).

#### 🎮 Core Applications & Microservices
| Command | Aliases | Description |
|---|---|---|
| `kongor play` | `willowmaker`, `launcher` | Launch WILLOWMAKER game client launcher (DXVK Vulkan 1.3, 1000 FPS patch, CDN sync) |
| `kongor host` | `compel`, `server` | Launch COMPEL dedicated server supervisor (1.0ms timer pacer, Win32 Job Object sandbox) |
| `kongor gui` | `control-panel`, `nexus`, `admin` | Launch NEXUS desktop administration GUI (Avalonia 11.2.3, 3D hero viewer, secrets vault) |
| `kongor start` | `aspire`, `cluster` | Launch local .NET Aspire 13.6.5 backend cluster (`https://localhost:5550`) |
| `kongor docker [action]` | `stack`, `docker-stack` | Turnkey Docker Compose stack: Backend + Match Fleet (`:11334`) + Queue Arcade (`:11484`) + optional `--client` (`up`, `down`, `status`, `logs`, `check`, `init-secrets`; `--perf Eco\|Balanced\|High`) |
| `kongor build [target]` | `compile`, `rebuild` | Build .NET projects (`apps`, `services`, `libs`, `tools`, `tests`) or native C++ layer (`kongor build native`) |
| `kongor master-server` | `master` | Run KONGOR Master Server standalone (SRP-6a authentication, MERRICK multi-DB) |
| `kongor chat-server` | `chat` | Run TRANSMUTANSTEIN TCP Chat Server standalone (zero-copy `System.IO.Pipelines`, ports `11031-11033`) |
| `kongor web-api` | `api` | Run ZORGATH Web Portal REST API backend standalone (Scalar OpenAPI at `/scalar/v1`) |
| `kongor web-ui` | `web` | Run DAWNBRINGER Web Portal MVC UI standalone |

#### 📦 Modding, Asset Pipeline & Creative Tools
| Command | Aliases | Description |
|---|---|---|
| `kongor mod <subcmd>` | `mod-cli` | HoN mod package manager (`list`, `enable`, `disable`, `inspect`, `apply`, `rollback`, `verify`) |
| `kongor prebake <subcmd>` | `prebake-tool` | Entity XML harvesting & MemoryPack+Zstd binary cache generator (`all`, `entities`, `lua`, `bundle`) |
| `kongor model [cmd]` | `models` | Export HoN heroes/avatars/animations to glTF/GLB (`kongor model export "thriller rhapsody"`) |
| `kongor blender [cmd]` | `launch-blender` | Launch Blender 3D (v4.2+ LTS) or manage addon lifecycle (`install`, `uninstall`, `stop`) |
| `kongor comfy [cmd]` | `comfyui` | Manage ComfyUI generative AI server on port `8188` (`install`, `uninstall`, `stop`) |
| `kongor replay [cmd]` | `render` | *(Experimental / WIP)* Render 60 FPS replay MP4 or detect teamfight highlights |
| `kongor highlight <file>` | `teamfight` | *(Experimental / WIP)* Automated teamfight highlight clip generator |

#### 🔬 Diagnostics, Testing & Operations
| Command | Aliases | Description |
|---|---|---|
| `kongor test [target]` | `tunit`, `xunit` | Run lock-serialized xUnit test suites (`--no-build`, `--build`, `--coverage`, `--filter <expr>`) |
| `kongor gate` | — | Run the 6-tier pre-completion quality gate (`scripts/gate_check.ps1`) |
| `kongor where` | — | Show the resolved workspace root, layout kind (`SourceCheckout` / `BundledInstall`), and resolution source |
| `kongor workspace [cmd]` | `ws` | Manage registered workspaces in `~/.kongor/config.json` (`current`, `list`, `use`, `add`, `remove`, `prune`) |
| `kongor completion <shell>` | — | Emit static shell tab-completions (`powershell`, `bash`, `zsh`, `fish`) |
| `kongor soak` | `endurance` | Execute automated socket & memory endurance soak testing |
| `kongor scan` | `vuln-scan` | Scan NuGet dependencies for known CVEs and emit SARIF 2.1.0 report |
| `kongor sbom` | `bom` | Generate CycloneDX Software Bill of Materials report (`artifacts/sbom.json`) |
| `kongor audit-endpoints` | `audit` | Extract Lua/XML `SubmitForm` routes & reconcile endpoints against Master Server |
| `kongor lua-verify` | `lua` | Headless NLua sandbox mocking K2 UI widgets, Cvars & PHP/Base64 serialization |
| `kongor k2-test` | `k2` | Headless K2 engine & cvar simulator |
| `kongor stop [target]` | `kill`, `shutdown` | Stop running services (`dotnet`, `docker`, `comfy`, `blender`, `all`) |
| `kongor secret [cmd]` | `secrets`, `config` | Manage User Secrets vault (`init`, `set <k> <v>`, `get <k>`, `list`, `remove <k>`, `clear`) |
| `kongor logs [service]` | `log`, `tail-logs` | Inspect and stream logs from microservices or dedicated servers (`-t`, `-n`) |
| `kongor doctor` | `check` | Diagnose port availability, .NET SDKs, native builds, and dependencies |
| `kongor status` | `info` | Display ecosystem topology, active ports, PIDs, and health status |
| `kongor docs` | `docfx`, `doc` | Build & launch interactive DocFX developer documentation hub (`http://localhost:8080`) |
| `kongor ask <question>` | `?`, `ai`, `q` | Natural-language semantic search across the Project KONGOR V codebase |

</details>

---

## 📸 Visual Showcase & Screenshots

<div align="center">

| 🌐 DAWNBRINGER Portal & Match History | 📊 Player Telemetry & Ranked Ladders |
| :---: | :---: |
| <img src="docs/screenshots/02_matches.png" alt="Match History & Timelines" width="460" /> | <img src="docs/screenshots/03_stats.png" alt="Player Performance Telemetry" width="460" /> |
| *Match scorecards, item progression & ward placements* | *KDR, creep score, win percentages & hero radar* |

| 📘 Hero Codex & Build Guides | 🛒 Merrick's Goblin Store & 3D Armory |
| :---: | :---: |
| <img src="docs/screenshots/06_hero_detail.png" alt="Hero Codex" width="460" /> | <img src="docs/screenshots/11_store.png" alt="Merrick's Store" width="460" /> |
| *Hero attributes, win-rate telemetry, skills & guides* | *Interactive 3D pedestal showcase, avatars & cosmetics* |

| 🖥️ NEXUS Control Panel Dashboard | 🔐 Secrets & Fleet Configuration Vault |
| :---: | :---: |
| <img src="docs/screenshots/16_control_panel_dashboard.png" alt="NEXUS Control Panel Dashboard" width="460" /> | <img src="docs/screenshots/17_control_panel_secrets.png" alt="NEXUS Secrets Vault" width="460" /> |
| *Docker Stack & Aspire supervision, health pills & live logs* | *Masked developer credentials, ports & COMPEL fleet settings* |

</div>

<details>
<summary>🖼️ <strong>View More Portal Screenshots (Leaderboard, Codex Grid, Items, Armory, Inventory & Plinko)</strong></summary>

<div align="center">

| 📊 Ranked Leaderboard (MMR / PSR) | 📖 Hero Codex Roster |
| :---: | :---: |
| <img src="docs/screenshots/04_leaderboard.png" alt="Ranked Leaderboard" width="460" /> | <img src="docs/screenshots/05_codex.png" alt="Hero Codex Roster" width="460" /> |

| 🛡️ Item Encyclopedia | 🎒 Player Armory & Couriers |
| :---: | :---: |
| <img src="docs/screenshots/07_items.png" alt="Item Encyclopedia" width="460" /> | <img src="docs/screenshots/10_armory.png" alt="Player Armory" width="460" /> |

| 🎁 Account Inventory | 🎯 Plinko Minigame |
| :---: | :---: |
| <img src="docs/screenshots/12_inventory.png" alt="Account Inventory" width="460" /> | <img src="docs/screenshots/13_plinko.png" alt="Plinko Minigame" width="460" /> |

</div>

</details>

---

## 🎯 What Are You Looking To Do? (The Decision Funnel)

<div align="center">
  <img src="docs/images/decision_funnel.svg" alt="ProjectKongor Decision Funnel" width="100%" />
</div>

* **🎮 Play (`WILLOWMAKER`):** Differential CDN patching, 1000 FPS unlock (`EnginePatchService`), DXVK Vulkan 1.3 translation, and clean `override/` preservation — [WILLOWMAKER Guide](src/apps/willowmaker/README.md).
* **🖥️ Host Dedicated Servers (`COMPEL`):** Win32 Job Object sandboxes, 1.0 ms multimedia timer pacer, anti-DDoS UDP proxy, and P-core pinning — [COMPEL Guide](src/apps/compel/README.md) & [COMPEL Chat GUI](src/apps/compel-gui/README.md).
* **⚙️ Run & Manage the Stack (`Docker` / `Aspire` / `Control Panel`):** Launch the 7-container Docker stack ([Docker Guide](infra/docker/README.md)), local `.NET Aspire 13.6.5` dashboard ([Aspire Guide](src/services/aspire-host/README.md)), or Avalonia `11.2.3` desktop dashboard ([Control Panel Guide](src/apps/control-panel/README.md)).

---

## 🗺️ Ecosystem Architecture & Data Flows

<div align="center">
  <img src="docs/images/ecosystem_architecture.svg" alt="ProjectKongor Ecosystem Architecture & Data Flows" width="100%" />
</div>

<details>
<summary>📐 <strong>Explore All 9 Subsystem Architecture Diagrams (Game Loop, Aspire, COMPEL, WILLOWMAKER, Master Server, Modding, Replay & Combat Juice)</strong></summary>

<div align="center">

| Diagram | Subsystem | Canonical Docs |
|---|---|---|
| **Game Loop & Vulkan Modernization** | 1.0 ms engine pacing, DXVK Vulkan 1.3 & low-latency input | [`WILLOWMAKER`](src/apps/willowmaker/README.md) |
| **WILLOWMAKER Pipeline** | Client launch, CDN manifest sync & 1000 FPS patching | [`WILLOWMAKER`](src/apps/willowmaker/README.md) |
| **COMPEL Lifecycle** | Dedicated worker supervisor, Job Object sandbox & UDP proxy | [`COMPEL`](src/apps/compel/README.md) |
| **NEXUS Control Panel** | Avalonia 11.2.3 GUI, Docker Stack, Three.js 3D viewer & vault | [`NEXUS Control Panel`](src/apps/control-panel/README.md) |
| **KONGOR Master Server** | SRP-6a auth, MERRICK multi-DB, Goblin store & stats | [`KONGOR Master Server`](src/services/master-server/README.md) |
| **.NET Aspire Orchestration** | Local microservice topology, Garnet/Redis & OpenTelemetry | [`ASPIRE AppHost`](src/services/aspire-host/README.md) |
| **Mod Pipeline** | `.honmod` DAG resolution, XML merging & VFS precedence | [`Kongor.Modding`](src/extensions/modding/README.md) |
| **Replay Renderer** | Vulkan swapchain capture, WASAPI loopback & NVENC MP4 | [`NEXUS.ReplayRenderer`](src/extensions/replay-renderer/README.md) |
| **Combat Juice Telemetry** | Teamfight spike detection & highlight scoring | [`Replay Studio`](src/apps/replay-studio/README.md) |

<img src="docs/images/game_loop_architecture.svg" alt="Game Loop Architecture" width="100%" />
<img src="docs/images/willowmaker_pipeline.svg" alt="WILLOWMAKER Engine Modernization Pipeline" width="100%" />
<img src="docs/images/compel_lifecycle.svg" alt="COMPEL Dedicated Server Lifecycle" width="100%" />
<img src="docs/images/control_panel_architecture.svg" alt="NEXUS Control Panel Architecture" width="100%" />
<img src="docs/images/master_server_architecture.svg" alt="KONGOR Master Server Architecture" width="100%" />
<img src="docs/images/aspire_orchestration.svg" alt=".NET Aspire Local Cluster Orchestration" width="100%" />
<img src="docs/images/mod_pipeline_architecture.svg" alt="HoN S2Z Mod Pipeline Architecture" width="100%" />
<img src="docs/images/replay_renderer_architecture.svg" alt="Replay Renderer Architecture" width="100%" />
<img src="docs/images/combat_juice_architecture.svg" alt="Combat Juice Architecture" width="100%" />

</div>

</details>

---

## 🧭 Ecosystem Navigation Matrix

| Component | Directory | Primary Role | Documentation |
|---|---|---|---|
| **Kongor CLI & TAO MCP** | `src/tools/kongor-cli/`, `src/libs/protocol-core/` | Unified CLI orchestrator, SRP-6a protocol core & TAO MCP server | [Kongor CLI](src/tools/kongor-cli/README.md) • [TAO MCP](docs/tao-mcp/overview.md) |
| **NEXUS Control Panel** | `src/apps/control-panel/` | Avalonia 11.2.3 desktop admin GUI, Docker launcher, 3D viewer, secrets | [Control Panel](src/apps/control-panel/README.md) |
| **WILLOWMAKER** | `src/apps/willowmaker/` | Game client launcher, differential CDN sync, 1000 FPS, DXVK Vulkan 1.3 | [WILLOWMAKER](src/apps/willowmaker/README.md) |
| **COMPEL & COMPEL Chat** | `src/apps/compel/`, `src/apps/compel-gui/` | Dedicated server supervisor, Win32 sandbox, UDP proxy & host console | [COMPEL](src/apps/compel/README.md) • [COMPEL GUI](src/apps/compel-gui/README.md) |
| **NEXUS Replay Studio** | `src/apps/replay-studio/` | Experimental camera spline director & 60 FPS MP4 highlight exporter | [Replay Studio](src/apps/replay-studio/README.md) |
| **KONGOR Master Server** | `src/services/master-server/` | Central master server, SRP-6a auth, MERRICK multi-DB, stats & Goblin store | [Master Server](src/services/master-server/README.md) |
| **.NET Aspire Host** | `src/services/aspire-host/` | Local cloud-native microservice orchestrator (`Aspire 13.6.5`, `:5550`) | [Aspire Host](src/services/aspire-host/README.md) |
| **TRANSMUTANSTEIN Chat** | `src/services/chat-server/` | Zero-copy `System.IO.Pipelines` binary TCP chat, channels & match coordinator | [Chat Server](src/services/chat-server/README.md) |
| **ZORGATH Web API & DAWNBRINGER UI** | `src/services/web-api/`, `src/services/web-ui/` | REST API (`Scalar /scalar/v1`) & ASP.NET Core MVC community portal | [Web API](src/services/web-api/README.md) • [Web UI](src/services/web-ui/README.md) |
| **Native Interop & Proxies** | `native/`, `native/proxies/` | CMake C++/MASM shims (`MPL-2.0`): `NativeGuard`, Vulkan capture, zlib, audio | [Native Layer](native/README.md) • [Proxies](native/proxies/README.md) |
| **Modding, AssetDB & Export** | `src/extensions/`, `tools/` | `.honmod` / `.s2z` engine, AssetDB CAS, `pkv-v5` CDN export, Blender addon | [Modding](src/extensions/modding/README.md) • [AssetDB](tools/assetdb/README.md) • [Export](tools/export/README.md) |
| **Cloud, Docker & K8s/K3s** | `infra/docker/`, `infra/k8s/`, `infra/k3s/` | Turnkey Compose stack, Agones `Fleet` autoscaling & bare-metal `k3s` scripts | [Docker](infra/docker/README.md) • [K8s](infra/k8s/README.md) • [K3s](infra/k3s/README.md) |

---

## 🏆 GG WP: Dedicated to the Players & Builders

* **To S2 Games & Frostburn Studios:** Thank you for creating one of the fastest, most mechanically demanding, and exhilarating competitive games ever made.
* **To the Kongor Studios Team:** To the developers working on **HoN Reborn**—we openly share the cleanroom platform services, protocol documentation, and developer tools we can release here in hopes any piece proves helpful. **It's all love here! ❤️**
* **To the Community & Pioneers:** Read our full [**Credits, Gratitude & Dedication Document**](docs/credits-and-gratitude.md).

---

## 📜 The "Always and Forever Free" Social Contract & Multi-License Structure

Project KONGOR V (`PKV`) will **always and forever remain 100% free and open-source**—zero donations, zero paywalls, zero monetization ([Preservation Charter](docs/charter.md)).

- **Platform Services, Desktop Apps, Tools, Scripts & Tests (Repository Root):** [**`AGPL-3.0-or-later`**](LICENSE) (honoring foundational upstream `[K]ONGOR` open-source lineage; `src/tools/blender/io_scene_hon/` under `GPL-3.0-or-later`).
- **Native Engine Modernization Layer (`native/`):** [**`MPL-2.0`**](native/LICENSE) (`RmlUi` & `libdeflate` submodules under `MIT`).
- **Documentation (`docs/`):** [**`CC BY-NC-SA 4.0`**](docs/LICENSE.md).
- **Governance & Legal Notices:** See [`NOTICE`](NOTICE), [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md), [`NAMING_POLICY.md`](NAMING_POLICY.md), [`SECURITY.md`](SECURITY.md), [`INTEROPERABILITY_AND_DMCA.md`](INTEROPERABILITY_AND_DMCA.md), and [`REUSE.toml`](REUSE.toml).
