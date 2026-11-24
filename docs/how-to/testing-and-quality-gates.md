# 🧪 How-To: Testing, Quality Gates & E2E UI Verification

> [!NOTE]
> This guide consolidates all automated testing workflows across Project Kongor: the **C# xUnit test suite** (`14` unit test assemblies in `sln-filters/Kongor.UnitTests.slnf` alongside Integration, GUI, Performance, Load, and E2E suites), the **Opt-In Manual/Slow Test Gate (`KONGOR_RUN_MANUAL_TESTS=1`)**, the **Memory-Safe Pre-Push Hook (`scripts/git-pre-push.ps1`)**, the **Sub-5s 6-Tier Inner-Loop Quality Gate (`scripts/gate_check.ps1`)**, and the **Client UI Restoration E2E Suite (`tests/run_e2e_ui_tests.ps1`)**.

---

## 1. C# Unit & Integration Test Suite (14 Unit Test Assemblies)

Project Kongor maintains comprehensive automated tests across 14 xUnit unit test assemblies in [`sln-filters/Kongor.UnitTests.slnf`](../../sln-filters/Kongor.UnitTests.slnf) covering Applications, Microservices, Libraries, Extensions, and Developer Tools:

| Test Project | Category | Scope & Coverage |
| :--- | :--- | :--- |
| `Kongor.Core.Tests` | Libraries | Protocol, SRP-6a, PHP Serializer, S2Z, Netcode |
| `NEXUS.Contracts.Tests` | Libraries | Shared DTOs, CQRS Contracts & Serialization |
| `MERRICK.DatabaseContext.Tests` | Libraries | Multi-DB EF Core Repositories & Migrations |
| `Kongor.Mcp.Tests` | Tools | TAO MCP Tools, Mailbox IPC, VFS Index |
| `Kongor.Cli.Tests` | Tools | Unified `kongor` CLI Command Routing & Diagnostics |
| `ReplayStudio.Tests` | Tools / Apps | Replay Studio Timeline, Spline Director & Export |
| `WILLOWMAKER.Tests` | Apps | Client Launcher, Named Pipe IPC, Patcher |
| `NEXUS.ControlPanel.Tests` | Apps | Avalonia GUI ViewModels, Vault, 3D Viewer |
| `COMPEL.Tests` | Apps | Fleet Supervisor, UDP Proxy, Job Sandbox |
| `NEXUS.MasterServer.Tests` | Services | Match Pipeline, OpenSkill, MERRICK DB |
| `TRANSMUTANSTEIN.ChatServer.Tests` | Services | Binary TCP Framing, Presence, Clans |
| `ZORGATH.WebPortal.API.Tests` | Services | REST API Controllers, JWT, Rate Limiter |
| `Kongor.Modding.Tests` | Extensions | `.honmod` Manager, DAG Conflict Resolver |
| `ReplayRenderer.Tests` | Extensions | Highlight Detector, Vulkan Capture, FFmpeg |

*(Additional suites in `ProjectKongor.slnx`: `ASPIRE.Integration.Tests`, `ASPIRE.Client.Integration.Tests`, `ASPIRE.Prebaking.Tests`, `Grpc.Challenger.Tests`, `ASPIRE.Client.Gui.Tests`, `ASPIRE.Tests.Performance`, `ASPIRE.Load.Tests`, `Kongor.Mcp.E2E.Tests`, and `AdversarialLuaVerifier`.)*

---

## 2. Running Tests (`kongor test` & Opt-In Manual/Slow Test Gate)

### 2.1 Default Fast Inner-Loop Execution
By default, tests taking longer than `2.0 seconds` (excluding one-time assembly initialization) are gated via `[Trait("Category", "Manual")]` and `if (!NexusTestEnvironment.IsManualTestEnabled) return;` so daily test runs and `git push` stay fast:

```powershell
# Run the fast unit test suite via Kongor CLI
.\kongor.ps1 test unit --no-build

# Or run directly under the singleton test-runner mutex
.\scripts\Invoke-KongorTestLock.ps1 -Owner "dev" -- dotnet test sln-filters/Kongor.UnitTests.slnf --filter "Category!=Manual" --no-build
```

### 2.2 Running All Tests Including Manual / Slow / Stress Tests (`KONGOR_RUN_MANUAL_TESTS=1` / `NEXUS_RUN_MANUAL_TESTS=1`)
To include heavy stress, concurrency, and multi-process CLI tests (guarded by [`NexusTestEnvironment.IsManualTestEnabled`](../../tests/shared/NexusTestEnvironment.cs) and `[Trait("Category", "Manual")]`):

```powershell
$env:KONGOR_RUN_MANUAL_TESTS = "1"
.\kongor.ps1 test --all --no-build
Remove-Item Env:\KONGOR_RUN_MANUAL_TESTS
```

### 2.3 Logging Full Test Sessions to a `.trx` Report
```powershell
$env:DOTNET_gcServer = "0"
dotnet test ProjectKongor.slnx -c Release --no-build --logger "trx;LogFileName=full_test_session.trx" --results-directory "artifacts/test-results"
```

---

## 3. Windows Virtual Commit-Charge Protection (`DOTNET_gcServer=0`)

> [!WARNING]
> **Why Server GC Causes `OutOfMemoryException` on Multi-Core Workstations:**
> On a 16–32 logical core machine, `.NET Server GC` (`DOTNET_gcServer=1`) reserves separate multi-gigabyte heap segments per CPU core for every `MSBuild.exe`, `VBCSCompiler.exe`, and `testhost.exe` process. Running `git push` while game clients or Docker containers are open can exhaust the Windows pagefile commit limit (`0x8007000E`), crashing `testhost.exe` with `OutOfMemoryException` or `FastFail`.

Both [`kongor.ps1`](../../kongor.ps1) and [`scripts/git-pre-push.ps1`](../../scripts/git-pre-push.ps1) automatically enforce:
1. **`$env:DOTNET_gcServer = "0"` (Workstation GC):** Reduces per-process virtual commit reservation from `3.2 GB` to `<250 MB`.
2. **`-maxcpucount:4` & `-nodereuse:false`:** Bounds concurrent MSBuild worker nodes and ensures zero lingering compiler daemons after build/test completion.
3. **`dotnet build-server shutdown`:** Automatically releases any background Razor/VBCSCompiler servers before and after the pre-push gate.

---

## 4. Sub-5-Second Inner-Loop Client Gate (`scripts/gate_check.ps1`)

Whenever modifying Lua scripts, K2 XML interfaces, `.s2z` archives, or protocol schemas, run the 6-tier inner-loop gate:

```powershell
# 1. Run 6-tier client & archive verification (<5 seconds)
powershell -ExecutionPolicy Bypass -File scripts/gate_check.ps1

# 2. Verify C# <-> Lua/Native protocol schema parity
powershell -ExecutionPolicy Bypass -File scripts/re/test_metagen_parity.ps1
```

---

## 5. Client UI Restoration E2E Test Suite (`166` Tests Across 4 Tiers)

The opaque-box End-to-End (E2E) Client UI Test Suite (`tests/client_ui_e2e/`) validates all 15 user-visible UI restoration features without requiring a live graphical window by executing real binary assets, `luac51.exe -p` bytecode compilation, `LuaVerifier.exe` NLua sandboxes, K2 XML package parsers, and DDS binary header inspectors.

### 5.1 Directory Layout
```text
tests/
├── run_e2e_ui_tests.ps1                 # Master PowerShell entry point
└── client_ui_e2e/
    ├── e2e_ui_framework.py              # VFSResolver, PackageParser, DDSInspector, LuaRunner
    ├── run_ui_e2e.py                    # Multi-tiered CLI runner with filtering & JSON telemetry
    ├── test_tier1_feature_coverage.py   # Tier 1: 75 Feature isolation tests (5 per feature)
    ├── test_tier2_boundary_corner.py    # Tier 2: 75 Boundary & stress tests (5 per feature)
    ├── test_tier3_cross_feature.py      # Tier 3: 10 Pairwise cross-feature interaction tests
    └── test_tier4_real_world_flows.py   # Tier 4: 6 End-to-end real-world user journey flows
```

### 5.2 Executing the E2E UI Suite
```powershell
# Run all 166 tests across Tiers 1-4
powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1

# Filter by Tier (1-4), Feature (1-15), or Keyword
powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1 -Tier 1
powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1 -Feature 6
powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1 -Filter "Midwars" -Verbose
```

### 5.3 15-Feature Coverage Matrix
| Feature ID | Feature Name | Tier 1 | Tier 2 | Tier 3 | Tier 4 | Total Tests |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **F01** | Centralized Screenshot Coordinator (`screenshot_coordinator.ps1`) | 5 | 5 | 1 | 1 | 12 |
| **F02** | Telemetry & Crash Isolation Hooks (`client_lua_debug.log`, `triage_client_logs.ps1`) | 5 | 5 | 0 | 0 | 10 |
| **F03** | Main Menu Laser Billboard Removal (`background.package`, `homepage_v2.package`) | 5 | 5 | 1 | 1 | 12 |
| **F04** | Main Menu Ambient Glow Restoration (`grad_glow2.tga`, `main_v2.interface`) | 5 | 5 | 0 | 0 | 10 |
| **F05** | Main Menu Logo & Pedestal Parity (`hero_login_controller.lua`, `logo_3d.model`) | 5 | 5 | 0 | 0 | 10 |
| **F06** | Store Category Tabs White Box Fix (`store2.package`, `.dds` migration) | 5 | 5 | 1 | 1 | 12 |
| **F07** | Store 3D Pedestal Checkerboard Fix (`background_store.package`, modelpanel) | 5 | 5 | 0 | 0 | 10 |
| **F08** | Missing Vanity & Preview Icons Fix (`accountvanity/`, `gamevanity/`, DDS headers) | 5 | 5 | 0 | 0 | 10 |
| **F09** | Stats Profile Open Defect Fix (`main_v2.lua`, `ShowPlayerStats`, `Player_Stats_V2`) | 5 | 5 | 1 | 0 | 11 |
| **F10** | Ranked Gold vs Public White Styling (`player_stats_v2.lua`, hex color styling) | 5 | 5 | 0 | 0 | 10 |
| **F11** | Hero Mastery & Learnatorium Restoration (`learnatorium.lua`, mastery grid) | 5 | 5 | 1 | 1 | 12 |
| **F12** | Midwars Blown-Out Lighting Fix (`midwars.s2z/worldconfig`, Vulkan clamp $\le 1.2$) | 5 | 5 | 1 | 1 | 12 |
| **F13** | Midwars Checkerboard Terrain Fix (`midwars.s2z/texturelist`, remove `$checker`) | 5 | 5 | 0 | 0 | 10 |
| **F14** | FoC & Midwars Post-Match Scoreboard (`match_stats_v2.lua`, endgame UI) | 5 | 5 | 0 | 0 | 10 |
| **F15** | Performance & Texture Cache Optimization (sub-1ms frame budget, LRU eviction) | 5 | 5 | 1 | 1 | 12 |
| **TOTAL** | **All 15 Features** | **75** | **75** | **10** | **6** | **166** |
