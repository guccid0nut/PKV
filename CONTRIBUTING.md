# 🤝 Contributing to Project Kongor

Welcome to Project Kongor! This document outlines engineering guidelines, local development workflows, architectural patterns, and testing conventions across our multi-project ecosystem.

---

## 🛠️ Prerequisites

* **.NET SDK:** .NET 11 SDK (`11.0.100` pinned in `global.json`)
* **PowerShell:** PowerShell 7+ (`pwsh`) for unified repository orchestration scripts
* **Docker:** Docker Desktop or Docker Engine (for containerized game servers and Testcontainers)
* **CMake & MSVC/GCC:** CMake 3.20+ with MSVC 2022 (Windows) or GCC/Clang (Linux) for native C++ shims
* **Tailwind Standalone CLI:** Run `./kongor.ps1 tailwind setup` on first clone to fetch the standalone Tailwind v4 CLI binary into `src/services/web-ui/tools/`
* **Optional Agent & Asset Tooling:** Run `./scripts/Install-OptionalAgentTools.ps1` (see [docs/getting-started/optional-agent-tools.md](docs/getting-started/optional-agent-tools.md)) to install optional 3D, audio, proxy, and search MCP integrations
* **Optional:** K3s + Agones (for production fleet management)

---

## 🚀 Quick Start Commands

All ecosystem tasks are routed through the central `./kongor.ps1` PowerShell entry point:

```powershell
# Run ecosystem doctor & dependency health check
./kongor.ps1 doctor

# Build all .NET projects across Debug and Release configurations
./kongor.ps1 build

# Build unified native C++ layer (version.dll, Vulkan layer, Lua 5.1 compiler)
./kongor.ps1 build native

# Launch the complete cloud cluster via .NET Aspire Host (Master Server, Web API, Chat, Garnet/Redis, DB)
./kongor.ps1 aspire

# Run test harnesses (unit, integration, performance, soak)
./kongor.ps1 test unit
./kongor.ps1 test integration
./kongor.ps1 test soak

# Build production minified Tailwind CSS v4 bundle
./kongor.ps1 tailwind build

# Watch Tailwind CSS in real time during UI development
./kongor.ps1 tailwind watch
```

---

## 🏗️ Architecture & Decision Trail

* **5-Tier System Topology:** See [docs/architecture/ecosystem.md](docs/architecture/ecosystem.md) for full ecosystem architecture and service communication layers.
* **Autonomous Self-Healing Hexad:** See [docs/architecture/self-healing.md](docs/architecture/self-healing.md) for autonomous monitoring and recovery mechanisms.
* **Architecture Decision Records (ADRs):** See [docs/adr/](docs/adr/) for the context and trade-offs behind our cryptographic, infrastructure, and front-end decisions.
* **Operations & Runbooks:** See [docs/operations/troubleshooting.md](docs/operations/troubleshooting.md) and [docs/operations/deployment-runbook.md](docs/operations/deployment-runbook.md) for system triage and deployment runbooks.

---

## 📐 Code Style & Conventions

1. **CQRS with MediatR:**
   * All `IRequest<T>`, `ICommand<T>`, and `IQuery<T>` records belong in `src/libs/contracts/NEXUS.Contracts/CQRS/{Domain}/`.
   * Execution handlers (`IRequestHandler<TRequest, TResponse>`) belong exclusively in the service project's `Features/{Domain}/` directory.
2. **API Documentation:**
   * Always write `/// <summary>`, `/// <param>`, `/// <returns>`, and `[ProducesResponseType]` on public controller actions to feed the interactive Scalar API documentation (`/scalar/v1`).
3. **Logging:**
   * Log extensively via structured Serilog logging (`LogDebug`, `LogInformation`, `LogError`). Cluttered development logs are welcomed to accelerate rapid debugging.
4. **Platform Scripting:**
   * Use Windows PowerShell idioms (e.g. `Copy-Item`, `Select-String`, `New-Item`) instead of legacy Unix aliases (`cp`, `grep`).

---

## 🎯 Surgical Client Override & VFS Workflow (`games/client/override/`)

When modifying in-game K2 UI interfaces (`.interface`, `.package`), Lua scripts (`.lua`), or textures (`.dds`):
1. **Keep `games/client/override/` Clean (`0` Clutter Files at Baseline):** Never unpack entire `.s2z` archives into `override/`.
2. **Locate & Resolve in $<1\text{ ms}$ (`VfsIndexService`):** Use `kongor_vfs_locate` and `kongor_vfs_resolve` to identify which `.s2z` archive owns the target file.
3. **Surgical Single-File Extraction:** Extract only the specific file being edited via [`scripts/vfs/read_archive_entry.ps1`](scripts/vfs/read_archive_entry.ps1) into `games/client/override/<path>`, and mirror shared UI edits in `games/client/ClientPackageFiles/<path>`.
4. **Zero-Focus-Theft Hot-Reload:** Run `scripts/verify_client_lua.ps1` and `scripts/verify_xml_syntax.ps1`, then call `kongor_hot_reload` (`CompositeGameIpcBridge` $\to$ `NexusMailboxIpcBridge` + `NativeClientIpcBridge`).
5. **Inner-Loop Gate & On-Demand `.s2z` Bake:** Run [`scripts/gate_check.ps1`](scripts/gate_check.ps1). Either commit directly in `override/` or bake into `.s2z` via [`scripts/vfs/inject_override_to_s2z.ps1`](scripts/vfs/inject_override_to_s2z.ps1) (5-tier `.bak` rotation, revertible via [`scripts/vfs/revert_injected_override.ps1`](scripts/vfs/revert_injected_override.ps1)).

👉 Full step-by-step recipe: **[🎯 Surgical Client Override & VFS Workflow](docs/how-to/client-override-vfs-workflow.md)**.

---

## 📄 Contributor Licensing (Inbound = Outbound Directory Scope)

Project KONGOR V uses a directory-scoped open-source licensing structure (see [`NOTICE`](NOTICE)). By submitting a pull request, patch, or issue contribution, you certify that your contribution is original cleanroom work (containing **zero** retail *Heroes of Newerth* executables, `.s2z` archives, textures, models, audio, or proprietary flavor prose) and agree that it is licensed under the outbound license of the target directory:

* **`native/`** (excluding `native/deps/`): [**Mozilla Public License 2.0 (`MPL-2.0`)**](native/LICENSE)
* **`docs/`**: [**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 (`CC BY-NC-SA 4.0`)**](docs/LICENSE.md)
* **`src/tools/blender/io_scene_hon/`**: [**GNU General Public License v3.0 or later (`GPL-3.0-or-later`)**](src/tools/blender/io_scene_hon/LICENSE)
* **All other repository paths** (`src/`, `scripts/`, `tests/`, `infra/`, `tools/`): [**GNU Affero General Public License v3.0 or later (`AGPL-3.0-or-later`)**](LICENSE)

---

## 📦 Git & Repository Architecture

* **Public Cleanroom Source Repository (GitHub):** Contains 100% cleanroom open-source code, tests, scripts, and documentation (**zero Git LFS objects**, **zero retail game assets**, **zero prebuilt `.exe`/`.dll` binaries**). External contributors can clone `--recurse-submodules`, build, and run all unit and integration tests immediately.
* **Private Maintainer Vault & Manual Direct Export:** Maintainer development workspaces track local game installations (`games/client/`, `games/server/`) on a private remote and publish audited cleanroom snapshots exclusively via manual `./scripts/Invoke-PublicGithubExport.ps1` (8-gate verification, dry-run by default).
* **Working Tree Hygiene:** Transient caches (`shadercache/`, `logs/`, `replays/`, `*.dmp`) and local game overlays (`games/`) are ignored in cleanroom checkouts.

### Where things live

| Tier | What | How it gets there |
|---|---|---|
| Cleanroom GitHub Repo | Source code (`src/`, `native/`, `tests/`, `scripts/`), docs, `RmlUi` (`MIT`) submodule | `git clone --recurse-submodules` |
| Optional Agent & CLI Tooling | `clangd`, `texconv`, `es`, `ffmpeg`, `proximo`, `K2-Blender`, `voicebox` | `./scripts/Install-OptionalAgentTools.ps1` |
| Local Game Runtime (`games/` — Optional) | Your legally acquired `HoN v4.10.1` client/server installation | Set `GameClientPath` / `GameServerPath` in Control Panel (`./kongor.ps1 gui`) or place under `games/` |

### Git hooks, Test Mutex & Memory-Safe Test Execution (versioned, agent/MCP-safe)

When running builds or test suites from automated scripts or AI coding agents in a shared checkout, always execute through the singleton test-runner mutex (`./scripts/Invoke-KongorTestLock.ps1 -Owner "<id>" -- <command>`) so concurrent workers never collide on MSBuild `obj/`/`bin/` locks.

One-time per clone:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-git-hooks.ps1   # sets core.hooksPath=.githooks
```

`.githooks/pre-push` → [`scripts/git-pre-push.ps1`](scripts/git-pre-push.ps1):

1. **Cleanroom Inner-Loop Gate**: Runs [`scripts/gate_check.ps1`](scripts/gate_check.ps1) to verify banned symbols, PowerShell ASTs, schemas, and secret/PII hygiene.
2. **Override Preservation**: Leaves `games/client/override/` intact (never forces `.s2z` injection or file eviction on `git push`).
3. **Isolated Release Build (Workstation GC)**: Compiles the entire solution into an **isolated** `artifacts/prepush-gate` folder with `$env:DOTNET_gcServer="0"`, `-maxcpucount:4`, `-nodereuse:false`, and `dotnet build-server shutdown` — running services (`kongor.exe`) are never locked or killed, and Windows virtual commit-charge exhaustion (`OutOfMemoryException`) is prevented.
4. **Automated Unit Test Gate**: Runs the xUnit test harness (`sln-filters/Kongor.UnitTests.slnf`) against the isolated build artifacts. Heavy stress tests (`>2.0s`) are gated behind `KONGOR_RUN_MANUAL_TESTS=1`. See **[Testing, Quality Gates & E2E UI Verification](docs/how-to/testing-and-quality-gates.md)**.

Environment knobs:
- `KONGOR_PREPUSH_MODE`: `full` (default) | `build` (skip tests) | `mirror-only` (skip build+tests)
- `KONGOR_RUN_MANUAL_TESTS`: `1` (include slow `>2.0s` stress tests during `kongor test` or `dotnet test`)
- `KONGOR_PREPUSH_LOCK_MINUTES`: Mutex wait timeout for concurrent pushes (default: 30 min)
- Emergency bypass: `git push --no-verify`

