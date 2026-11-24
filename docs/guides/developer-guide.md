# 🛠️ Project Kongor Developer Guide & Engineering Invariants

Welcome to the **Project Kongor Developer Guide**. This document serves as the authoritative onboarding and development manual for engineers contributing to Project Kongor across the game client, dedicated server fleet, cloud microservices, and native tooling.

---

## 📋 1. Prerequisites & Environment Setup

### Required Toolchains
- **Operating System**: Windows 10 / 11 (x64) or Windows Server 2022+.
- **.NET SDK**: **.NET 11.0 SDK (`11.0.100` pinned in `global.json`)**.
- **Git**: Git `2.45+` (zero Git LFS dependencies).
- **IDE**: Visual Studio 2022 (v17.12+ with *.NET Desktop Development* & *C++ Desktop Development*) or JetBrains Rider 2025+.
- **Python**: Python 3.11+ (x64) with `numpy`, `scipy`, `Pillow`.
- **PowerShell**: Windows PowerShell 5.1 and PowerShell 7+ (`pwsh`).
- **GPU / Rendering**: Vulkan 1.3 compatible GPU driver (NVIDIA 550+, AMD Adrenalin 24.3+, or Intel Arc).
- **Docker / Minikube** (Optional for container hosting): Docker Desktop or Minikube for local k8s fleet testing.

### Initial Setup & Developer Onboarding

#### 1. Clone Repository (Cleanroom Zero-LFS)
Public clones require zero `.s2z` game archives or Git LFS pulls to build and run all services and unit tests:
```powershell
# 1. Clone repository with MIT submodules (RmlUi, libdeflate)
git clone --recurse-submodules https://github.com/guccidonut/PKV.git
cd PKV

# 2. Optional: Mount your own HoN 4.10.1 files for live client/server runtime features
.\scripts\vfs\Hydrate-LocalGameAssets.ps1 -GameDir "C:\Path\To\HoN"
```

#### 2. Restore Pinned Local Tools
Restore local dotnet CLI tools (Aspire CLI, EF Core) pinned in `.config/dotnet-tools.json`:
```powershell
dotnet tool restore
```

#### 3. Configure User Secrets
Project Kongor uses a centralized User Secrets Vault (`00005072-6f6a-6563-7420-4b4f4e474f52`) for the Aspire host, Docker stack, and microservices:
```powershell
# PostgreSQL / Supabase Database Connection
.\kongor.ps1 secret set "ConnectionStrings:MERRICK" "Host=your-postgres-host;Port=5432;Database=kongor;Username=...;Password=..."

# Distributed Cache Connection (Microsoft Garnet / Redis)
.\kongor.ps1 secret set "ConnectionStrings:DISTRIBUTED-CACHE" "localhost:28298,abortConnect=false"

# Safely generate cryptographic JWT, COMPEL, and internal service keys (.bak backup created automatically)
.\kongor.ps1 docker init-secrets
```
*(Note: Fallback defaults for local Aspire development are pre-configured in `src/services/aspire-host/appsettings.json`; the Docker stack enforces a strict fail-closed secret pre-flight via `.\kongor.ps1 docker check`.)*

#### 4. Run System Health Diagnostics & Build
```powershell
# Run system environment & health checks (auto-detects Aspire or kongor-stack Docker mode)
.\kongor.ps1 doctor

# Build solution
.\kongor.ps1 build
```

#### 5. Launch Full Stack or Client
```powershell
# Option A: Turnkey Docker Stack (Backend Cluster + Dedicated Match Fleet :11334 + Queue Arcade :11484 + Host Client)
.\kongor.ps1 docker up --perf Balanced --client

# Option B: Local .NET Aspire Orchestrator & Backend Services (:5550 dashboard)
.\kongor.ps1 aspire

# Launch WILLOWMAKER Game Client Launcher standalone (auto-discovers games/client)
.\kongor.ps1 play
```

---

## 📂 2. Repository Monorepo Structure

Project Kongor is organized into five clean architectural layers:

```
ProjectKongor/
├── src/
│   ├── apps/               # Desktop applications & server supervisors
│   │   ├── compel/         # Dedicated server fleet supervisor & UDP proxy
│   │   ├── compel-gui/     # Avalonia host chat & fleet console
│   │   ├── control-panel/  # Avalonia 11 desktop administration GUI
│   │   ├── replay-studio/  # Experimental Avalonia replay director & MP4 exporter
│   │   └── willowmaker/    # High-FPS client launcher & DXVK/Vulkan injector
│   ├── services/           # Backend microservices (.NET Aspire orchestration)
│   │   ├── aspire-host/    # Orchestrator & OpenTelemetry distributed dashboard
│   │   ├── chat-server/    # TRANSMUTANSTEIN TCP binary chat server (Ports 11031–11033)
│   │   ├── master-server/  # KONGOR SRP-6a coordinator & match pipeline (Port 55555)
│   │   ├── web-api/        # ZORGATH REST API with JWT & Discord OAuth2 (Port 5200)
│   │   └── web-ui/         # DAWNBRINGER ASP.NET MVC player portal (Port 5266)
│   ├── libs/               # Core shared libraries & data access
│   │   ├── contracts/      # DTOs, events, and RPC models
│   │   ├── database-context/ # MERRICK EF Core context (PostgreSQL, Supabase, SQL Server)
│   │   ├── protocol-core/  # Network framing, UDP crypto, audio engines, & IPC
│   │   └── service-defaults/# Aspire service discovery, health checks, & OpenTelemetry
│   ├── extensions/         # Engine plugins, modding, and render pipelines
│   │   ├── cdn-broker/     # Fast CDN patch synchronizer
│   │   ├── modding/        # Texture injection & S2Z runtime mounting
│   │   ├── replay-renderer/# Vulkan headless 60 FPS MP4 video encoder
│   │   ├── spatial-audio/  # OpenAL / HRTF 3D audio engine
│   │   └── vulkan-interop/ # Vulkan shared-memory frame receiver & glTF loader
│   └── tools/              # Developer tooling, CLI, and MCP servers
│       ├── kongor-cli/     # Unified CLI (Spectre.Console) & TAO MCP Host
│       ├── ghidra/         # Pre-baked Ghidra decompilation MCP server
│       └── headless-renderer/ # Offscreen UI visual testing tool
├── tools/                  # Non-.NET pipelines (assetdb, export, docgen, hon-model-tools, spectator-console)
├── native/                 # CMake C/C++/MASM proxies, Vulkan capture layer & luac51 (MPL-2.0)
├── infra/
│   ├── docker/             # Turnkey docker-compose.stack.yml, Dockerfiles, & socat entrypoint bridges
│   └── k8s/                # Kubernetes & Agones dedicated server fleet manifests
├── docs/                   # Diataxis developer documentation, how-to guides & roadmaps
├── tests/                  # Modular xUnit test assemblies (Kongor.UnitTests.slnf), UI E2E & Python suites
└── games/                  # Optional local client/server binaries & VFS overlays (git-ignored, BYOA)
```

---

## 🎯 3. Surgical Client Override & VFS Workflow (`games/client/override/`)

When developing or debugging in-game K2 UI interfaces (`.interface`, `.package`), Lua scripts (`.lua`), or textures (`.dds`):

1. **Keep `games/client/override/` Clean (`0` Clutter Files at Baseline):**
   - Never unpack entire `.s2z` archives into `override/`.
2. **Locate & Verify Precedence (`VfsIndexService`):**
   - Use `kongor_vfs_locate` (searches all `.s2z` ZIP Central Directories in $<1\text{ ms}$) and `kongor_vfs_resolve` (confirms precedence across `override` > `base` > `game/resources999..0.s2z` > `ClientPackageFiles` > `textures.s2z`).
3. **Surgical Single-File Extraction:**
   - Extract only the target file using [`../../scripts/vfs/read_archive_entry.ps1`](../../scripts/vfs/read_archive_entry.ps1) into `games/client/override/<path>`, and mirror shared UI edits in `games/client/ClientPackageFiles/<path>`.
4. **Pre-Flight Lint & Zero-Focus-Theft Hot-Reload:**
   - Run `scripts/verify_client_lua.ps1` and `scripts/verify_xml_syntax.ps1`.
   - Trigger `kongor_hot_reload` or `kongor_client_eval_lua` (`CompositeGameIpcBridge` $\to$ `NexusMailboxIpcBridge` + `NativeClientIpcBridge` over `version.dll` NativeGuard) to reload live in $<50\text{ ms}$ without stealing OS window focus.
5. **Inner-Loop Gate & On-Demand `.s2z` Injection:**
   - Run [`../../scripts/gate_check.ps1`](../../scripts/gate_check.ps1) (sub-5s 6-tier verification).
   - Commit directly in `override/` or bake into `.s2z` via [`../../scripts/vfs/inject_override_to_s2z.ps1`](../../scripts/vfs/inject_override_to_s2z.ps1) (5-tier `.bak` rotation, revertible via [`../../scripts/vfs/revert_injected_override.ps1`](../../scripts/vfs/revert_injected_override.ps1)).
   - 📖 Read the complete recipe: **[Surgical Client Override & VFS Workflow](../how-to/client-override-vfs-workflow.md)**.

---

## ⚡ 4. Fast Inner-Loop Testing, Manual Test Gating & Memory Safety

To maintain fast inner-loop test execution across all **14 unit test assemblies (`sln-filters/Kongor.UnitTests.slnf`)** and prevent Windows virtual commit-charge exhaustion:

### ⏱️ Opt-In Manual / Slow Test Gating (`> 2.0 Seconds`)
Any test that executes for longer than 2.0 seconds (excluding one-time assembly warmup), high-iteration stress tests, or child CLI process invocations is marked with `[Trait("Category", "Manual")]` and guarded by `NexusTestEnvironment.IsManualTestEnabled` (`tests/shared/NexusTestEnvironment.cs`):

```powershell
# Default fast inner-loop suite (runs fast tests; skips Category=Manual >2.0s tests)
.\kongor.ps1 test --no-build

# Opt-in full suite including manual/slow/stress tests
$env:KONGOR_RUN_MANUAL_TESTS = "1"
.\kongor.ps1 test --no-build
```

### 🛡️ Workstation GC & Commit-Charge Protection (`DOTNET_gcServer=0`)
Both [`../../kongor.ps1`](../../kongor.ps1) and [`../../scripts/git-pre-push.ps1`](../../scripts/git-pre-push.ps1) automatically enforce `$env:DOTNET_gcServer="0"` (Workstation GC), `-maxcpucount:4`, `-nodereuse:false`, and `dotnet build-server shutdown` so multi-core builds and test hosts never exhaust the Windows pagefile commit limit (`OutOfMemoryException`).
- 📖 Read the complete testing guide: **[Testing, Quality Gates & E2E UI Verification](../how-to/testing-and-quality-gates.md)**.

---

## 🎮 5. Strict Headless Client Testing & Offscreen Capture Protocol

When launching or testing the game client (`hon_x64.exe`):

1. **Always Use Headless Offscreen Coordinates or Background Capture**:
   - Launch with `+Set vid_pos -32000 -32000` or `window_mode: "offscreen"` for automated headless runs.
   - Use `kongor_capture_window` (Tier 0 Direct Vulkan Swapchain Shared Memory `VK_LAYER_HON_replay_capture` or Tier 1 Win32 `PrintWindow`) to capture frames with **0 ms dwell time and zero focus theft**.
2. **Perceptual Visual Baseline Diffing**:
   - Before and after UI changes, verify layout integrity using `kongor_ui_visual_diff` and `kongor_assert_visual`.

---

## 📜 6. K2 UI Automation & Lua Scripting Invariants

When authoring Lua scripts, UI modifications, or in-engine automations:

1. **Global UI Manager is `UIManager` (Never `Windows`)**:
   - `Windows` is not defined in K2 Lua.
   - Correct invocation: `UIManager.GetInterface('main'):GetWidget('widget_name')` (or `GetWidget('widget_name')` within interface script context).
2. **`DoEvent()` Signature Rules**:
   - `widget:DoEvent()` takes **no parameters**. It triggers the XML `onevent="..."` attribute.
   - `widget:DoEventN(index)` takes an integer index (`0`, `1`, `2`) to trigger `onevent0`, `onevent1`, etc.
   - **Never pass strings like `'onclick'` to `DoEvent`**: `onclick` is an input action or callback (`SetCallback('onclick', fn)`), not a `DoEvent` target.
3. **`<modelpanel>` Animation & `textures.s2z` Shadowing Footguns**:
   - Never set `anim="idle"` as a static XML attribute on a `<modelpanel>` (resets `StartTime` every tick); use `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`.
   - Legacy paths inside `games/client/game/textures.s2z` (`00000000/ui/...`) cannot be shadowed by identically named files in `override/`; use a versioned basename (e.g. `tab38_*.dds`).
4. **Anti-Chat Spill & Console Dismissal**:
   - Never dispatch commands via keyboard simulation when an in-game chat prompt (`>`) is active or focused. Use `kongor_ui_control action: "dismiss_console"` to deterministically close the console.

---

## 📡 7. Dedicated Server Command Routing

When routing console commands or cvars to dedicated game servers (`hon_x64.exe -dedicated`):
1. **Always Target Specific Ports**: Never broadcast or route commands to an unnamed pipe. Commands targeting dedicated servers MUST include the target listen port (e.g. `port: 11235`).
2. **Dual-Channel Execution**:
   - **Console Standard Input (`stdin`)**: Write directly to the worker's redirected stdin stream for instant console command execution.
   - **Dedicated Named Pipe**: If using IPC, connect to `\\.\pipe\hon-server-{port}`.
3. **Practice & Match Testing**: Use `AddFakePlayer <team>` (`AddFakePlayer 1` or `AddFakePlayer 2`) directed to the specific server port to populate slots deterministically.

---

## 💻 8. PowerShell & Scripting Standards

Per project standards:
- We develop on **Windows PowerShell** / **PowerShell 7**.
- Avoid Unix commands like `grep` or `copy`. Use idiomatic PowerShell cmdlets:
  - Use `Select-String` instead of `grep`.
  - Use `Copy-Item` instead of `copy` / `cp`.
  - Use `Get-ChildItem` instead of `find` / `ls`.
  - Use `Test-Path` instead of `test -f`.
- **Extensive Logging**: Log generously during development. Verbose diagnostics accelerate inner-loop root cause isolation.

---

## 🩺 9. Living Issue Backlog & Self-Healing Protocol

1. **Autonomous Log Triage (`docs/ISSUES.md`)**:
   - Whenever discovering errors or unhandled warnings in logs (`WILLOWMAKER.log`, `console.log`, crash dumps `diag_*.txt`, Aspire traces, browser console), actively record them into `docs/ISSUES.md`.
   - Each entry follows the structured format:
     ```markdown
     ### [ISSUE-###] Short Title
     * **Subsystem**: `Game Client` | `Dedicated Server` | `Compel` | `Willowmaker` | `Web UI / API`
     * **Discovery Method**: How the issue was identified.
     * **Symptom / Log Snippet**: Exact error timestamp and line.
     * **Root Cause Diagnosis**: Technical cause.
     * **Status**: `[OPEN]` | `[INVESTIGATING]` | `[PATCHED]` | `[RESOLVED]`
     * **Remediation & Verification**: Exact fix and test verification.
     ```
2. **Hunt-and-Fix Loop**:
   - **Probe**: Run automated health checks across all subsystems.
   - **Harvest**: Scrape and parse logs for exceptions.
   - **Catalog**: File discovered items into `docs/ISSUES.md`.
   - **Patch**: Implement minimal, high-precision code or config corrections.
   - **Validate**: Execute fast automated tests (`dotnet test`) and verify zero regressions.
