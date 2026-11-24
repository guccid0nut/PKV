# Project Kongor — Master Roadmap to 10/10 (Unified & Verified)

> **Status**: Active Engineering Roadmap (Diataxis Explanation Quadrant)  
> **Scope**: Consolidates repository verification audits, runtime stability findings, and structural quality ratchets into a single git-tracked reference.  
> **Code Anchor Convention**: References into [`scripts/gate_check.ps1`](../../scripts/gate_check.ps1) and engine hooks use searchable symbol/section anchors (`TIER 1`, `TIER 2`, `TIER 4`, `TIER 5`, `$trackedMirrorRoots`, `[Phase 19]`, `empty_catch_ratchet_by_dir`) rather than fragile line numbers.

---

## Executive Scorecard & Trajectory

| Dimension | Baseline | Post-P0 Target | Post-P3 Actual | Core Verification Anchor |
|---|---|---|---|---|
| **Reverse Engineering & Native Hooks** | 9.5 / 10 | 9.8 / 10 | **10 / 10** | `python scripts/validate_native_hooks.py` + lazy `miniz` fallback (`Hooked_CArchive_Open`) |
| **Performance & Rendering** | 9.0 / 10 | 9.6 / 10 | **10 / 10** | 144 FPS frame budget (`con_flushLog=false` preserved across `nexus_mailbox.lua`) |
| **Verification & Quality Gates** | 8.5 / 10 | 9.6 / 10 | **10 / 10** | `scripts/gate_check.ps1` (`SKIPPED: 0`, 6-tree parity, `.s2z` & DLL SHA-256 guards) |
| **Test Architecture** | 7.5 / 10 | 9.0 / 10 | **10 / 10** | `scripts/test_gate_mutants.ps1` + `Kongor.Mcp.Tests` (`McpToolContractTests` + `tests/golden/mcp_tools_list.json`) |
| **C# Architecture & Code Health** | 6.5 / 10 | 8.5 / 10 | **10 / 10** | Domain-modularized `McpToolRegistry.*.cs` (`1,412` lines core, down from `5,161`), `max_test_only_public_types: 158` (`-577`), and empty-catch burn-downs across `kongor-cli` (`-126`), `protocol-core` (`86 -> 31`), and `compel` (`86 -> 24`) |
| **Repo Hygiene & Knowledge Base** | 6.0 / 10 | 9.5 / 10 | **10 / 10** | `scripts/kongor_mcp_issues.ps1 -Action check` (`0` `kongor/*` issues in `[MCP-PROMOTE]`, 99 canonical tools with explicit `Required` & `Enum` schemas locked in `tests/golden/mcp_tools_list.json`) |

---

## P0 — Immediate High-Leverage Fixes (Implemented)

### 1. Single Canonical Git-Tracked Roadmap
- **Problem**: Planning state was previously split across IDE scratch files and untracked session artifacts, causing line-number drift and stale status tracking.
- **Resolution**: Consolidated into [`docs/explanation/roadmap-to-10.md`](../../docs/explanation/roadmap-to-10.md) using symbol/string anchors (`TIER 1`, `TIER 4`, `TIER 5`, `$trackedMirrorRoots`, `[Phase 19]`).

### 2. Revert 144 FPS Synchronous Log-Flush Regression & Enforce 6-Tree Parity
- **Problem**:
  1. `games/client/override/ui/scripts/nexus/nexus_mailbox.lua` previously executed global `Set('con_flushLog', 'true')`, `Set('log_flush', 'true')`, and `Set('sys_flushLogs', 'true')` at script load time, overriding `autoexec.cfg`'s asynchronous logging configuration and re-introducing synchronous disk-flush micro-stutters on every `println`.
  2. `games/shared/ui/scripts/` had drifted across 6 Lua files (`heartbeat_controller.lua`, `main_v2.lua`, `store_preview_stage.lua`, `newui/match_stats_v2.lua`, `newui/player_hosted_v2.lua`, `nexus/nexus_mailbox.lua`) because `games\shared` was omitted from `$trackedMirrorRoots` in `scripts/gate_check.ps1` (`TIER 5: Cross-Tree Asset Parity`).
- **Resolution**:
  - Removed the global `con_flushLog` / `log_flush` / `sys_flushLogs` overrides from `nexus_mailbox.lua` across all 5 trees where it resides.
  - Synchronized all 6 drifted Lua scripts into `games/shared/ui/scripts/`.
  - Added `games\shared` to `$trackedMirrorRoots` in `scripts/gate_check.ps1` (`TIER 5`) so all 6 VFS trees (`override`, `game`, `verify_lua`, `server`, `ClientPackageFiles`, `shared`) are verified for 0-drift parity on every gate run.

### 3. Make `gate_check.ps1` Honest (`SKIPPED: 0`, `.s2z` Byte Ceilings, & Deployed DLL SHA-256 Parity)
- **Problem**:
  1. `scripts/gate_check.ps1` (`[Phase 19]`) checked only temporary `*_probe.json` fixture paths, silently skipping 6 checks on normal live-repository runs while only printing `PASSED` / `FAILED` / `WARNINGS`.
  2. `TIER 4` checked `version.dll` / `vfs_mounts.json` existence but did not enforce `.s2z` 32-bit `CMMapUnzip` offset ceilings or SHA-256 parity across deployed copies of `version.dll` and `VkLayer_hon_replay_capture.dll`.
- **Resolution**:
  - **Live-Repo Phase 19 Fallbacks & `SKIPPED` Counter**: When `-TargetRoot` is not passed, `[Phase 19]` falls back to inspecting the live repository (`native/proxies/native_guard/dllmain.cpp`, `scripts/`, `docs/reference/field-notes/`, `docs/reference/mcp-issues/`, `data/schemas/protocol.json`, `artifacts/evidence/`) so `SKIPPED: 0` on live runs while preserving `*_probe.json` behavior for `scripts/test_gate_mutants.ps1`. The summary banner now reports `PASSED: x | FAILED: y | WARNINGS: z | SKIPPED: n`.
  - **Byte-Tiered `.s2z` Ceiling Guard (`TIER 4`)**:
    - `games/client/game/textures.s2z`: `WARN >= 1,900,000,000` bytes; `FAIL > 2,042,906,138` bytes (current baseline, frozen against growth toward `INT32_MAX`).
    - `games/**/resources*.s2z`: `FAIL >= 1,610,612,736` bytes ($1.5\text{ GiB}$ ceiling; current max is `resources94.s2z` at `1,546,291,511` bytes).
    - `games/**/resources99.s2z`: `WARN >= 1,200,000,000` bytes.
    - Any `*.s2z`: `FAIL >= 2,147,483,647` bytes (`INT32_MAX`).
  - **Deployed Native Binary SHA-256 Parity (`TIER 4`)**:
    - Verifies all 4 deployed copies of `version.dll` (`games/client/version.dll`, `games/client/windows-x64/version.dll`, `games/server/version.dll`, `games/server/windows-x64/version.dll`) share an identical SHA-256 hash.
    - Verifies both deployed copies of `VkLayer_hon_replay_capture.dll` (`games/client/VkLayer_hon_replay_capture.dll`, `games/client/windows-x64/VkLayer_hon_replay_capture.dll`) share an identical SHA-256 hash.

### 4. MCP Issue KB `verified_by:` Gate Invariant, Auto-Reopen on `bump`, & Honest Status Audit
- **Problem**: `docs/reference/mcp-issues/` contained issues marked `status: patched` or `status: resolved` without regression-test verification (`verified_by:`), including issues that subsequently recurred on `-Action bump`, plus 5 files with unstripped `# open | investigating | ...` YAML comments in `status:`.
- **Resolution**:
  - Updated [`scripts/kongor_mcp_issues.ps1`](../../scripts/kongor_mcp_issues.ps1) (`Parse-McpIssueFrontmatter`, `-Action check`, `-Action bump`):
    - Strips inline `# ...` comments when parsing frontmatter fields and rejects literal `#` inside `status:` during `-Action check`.
    - Requires a non-empty `verified_by:` field whenever `status:` is `patched` or `resolved` during `-Action check`.
    - Automatically demotes `status: patched` or `status: resolved` back to `status: investigating` (and clears `verified_by:`) whenever `-Action bump` is invoked on a recurring issue.
  - Audited all 239 `MCP-*.md` files: attached concrete `verified_by:` test/commit anchors to verified fixes (`MCP-001`, `MCP-235`, etc.) and demoted unverified claims to honest statuses (`investigating` / `documented`), then rebuilt `docs/reference/mcp-issues/index.json`.

### 5. Per-Directory Empty-Catch Ratchet, Python `src/tools/kongor` Scan, & Test-Only Public Type Ratchet
- **Problem**:
  1. `src/tools/kongor/mcp_server.py` lay outside `$pythonRoots` in `scripts/gate_check.ps1` (`TIER 1`) and contained 5 `except Exception: pass` blocks plus a swallowed `returncode` check.
  2. A single global `empty_catch_ratchet: 895` allowed new empty catches in one C# project to hide behind cleanups in another.
  3. Public types in `src/` referenced exclusively by `tests/` lacked a monotonic ceiling.
- **Resolution**:
  - Replaced all 5 silent `except Exception: pass` blocks in [`src/tools/kongor/mcp_server.py`](../../src/tools/kongor/mcp_server.py) with `sys.stderr.write` diagnostics and surfaced non-zero exit codes in `kongor_verify_lua`, then added `"src/tools/kongor"` to `python_scan_roots` in [`data/gate/slop_rules.json`](../../data/gate/slop_rules.json).
  - Replaced the single global C# empty-catch ceiling with `empty_catch_ratchet_by_dir` in `data/gate/slop_rules.json` (26 per-project ratchets) and wired `max_test_only_public_types` into `scripts/gate_check.ps1` (`TIER 1`, accelerated via `data/gate/symbols.cache`).

### 6. Lazy `miniz` Archive Fallback & Safe Directory/File Enumeration (`modern_archive_hook.cpp`)
- **Problem**:
  1. Eagerly opening every `< 1.5 GiB` `.s2z` archive with both K2's `CMMapUnzip` and `miniz` duplicated central-directory parsing and file handles across all ~100 archives.
  2. When native `CArchive::Open` (`0x67A620`) failed on a `> 2 GiB` / Zip64 / `.s2zdir` archive and `miniz` rescued it, or when `pInternal + 0x40` was overwritten on an already-open archive, native `std::vector<CMMapUnzip*>` buffers could leak or corrupt on `CArchive::Close` (`0x679E70`), and `CArchive::GetFileList` (`0x679270`) / `CArchive::GetNextFileAtPath` (`0x679410`) walked an empty red-black tree at `pInternal + 0x58`.
- **Resolution**:
  - **Lazy Fallback Policy**: `Hooked_CArchive_Open`, `Hooked_CArchive_ContainsFile`, `Hooked_CArchive_ReadFile`, and `ProactivelyScanFileManager` now leave healthy `< 1.5 GiB` standard archives 100% on K2's native `CMMapUnzip` path, registering a `ModernArchive` only when `!bK2Opened`, file size `>= 1,610,612,736` bytes ($1.5\text{ GiB}$), `.s2zdir` directory exists, or `KONGOR_FORCE_MINIZ_ALL=1` is set.
  - **Safe `pInternal + 0x40` Synthesis & Cleanup**: Synthesizes a non-empty `vUnzip` indicator at `pInternal + 0x40..0x48` **only** when `orig40 == orig48` (`modernArch->synthesized_unzip_vec = true`), and restores `[pInternal + 0x40..0x50]` in `Hooked_CArchive_Close` (and prior to re-open) before `g_origCArchiveClose` runs so `K2_Free` (`0x3F0D90`) never receives a synthetic pointer.
  - **Archive Metadata, Prefix Stripping & Enumeration Hooks**: Pre-disables whole-archive `MapViewOfFile` (`ARCHIVE_DISABLE_MMAP = 0x80`) on Zip64 / `>= 1.5 GiB` archives to prevent `0xC0000005` in `CMMapUnzip::Initialize` (`0x945EDF`), populates `m_sPath` (`+0x08`), `m_sPathToArchive` (`+0x28`), `m_sMod` (`+0x48`), and `m_sCompleteDiskPath` (`+0x88`) via K2's `K2StringW::assign` (`0x2A4890`), strips both master and `m_sPathToArchive` prefixes in `Hooked_CArchive_ContainsFile` / `Hooked_CArchive_ReadFile`, and hooks `CArchive::GetFileList` (`0x679270`) and `CArchive::GetNextFileAtPath` (`0x679410`) so `miniz`-backed archives enumerate seamlessly across `CFileManager::GetFileList`.
  - **Default-On Lazy Mode**: Enabled by default in `dllmain.cpp` (`InstallModernArchiveHooks`), guarded by Safe Mode and the `KONGOR_DISABLE_MINIZ_ARCHIVE=1` kill-switch.

---

## P1 — First-Call Tool Ergonomics & Issue Verification (Implemented)

1. **First-Call Success Rate Scoreboard & JSONL Telemetry (`P1.1`)**:
   - `McpToolRegistry` tracks per-tool first-call success rates (`GetFirstCallScoreboard()`) and appends structured JSONL call outcomes (`tool`, `firstCall`, `succeeded`, `durationMs`, `mcpIssueId`).
2. **Server-Side Preflight Checks & Self-Remediating Error Envelopes (`P1.2`)**:
   - `McpToolRegistry` enriches canonical tool descriptions with top `[MCP-NNN]` pitfalls at startup, runs sub-5ms preflight checks before stateful client/UI tools (`enforce_preflight` / `auto_heal`), and attaches `mcpIssueId`, `preventionRule`, and `remediationCommand` to error envelopes.
3. **Terse-by-Default (`< 200 tokens`) Tool Outputs (`P1.3`)**:
   - `HealthCheckHandler`, `VfsInspectHandler`, and `GetClientLogsHandler` default to `detail: "concise"` (`< 200 tokens`) while preserving `detail: "full"`.
4. **Deterministic Verification of `MCP-230` through `MCP-238` (`P1.4`)**:
   - All 9 issues (`MCP-230`..`MCP-238`) are verified by deterministic xUnit tests in [`tests/unit/tools/Kongor.Mcp.Tests/McpToolContractTests.cs`](../../tests/unit/tools/Kongor.Mcp.Tests/McpToolContractTests.cs) and marked `status: resolved` with `verified_by:` links.

---

## P2 — Tool Surface Consolidation, Ratchet Burn-Down & Core Issue Closure (Implemented)

1. **Canonical-Only `tools/list` & Domain Toolset Filtering (`P2.1`)**:
   - `McpToolRegistry.ListTools()`, `GetAllTools()`, `McpServer.HandleToolsList()`, and `KongorOfficialMcpAdapter` expose only the **99 canonical tools** (0 duplicate aliases, down from 224 total tools / 125 aliases) by default, cutting MCP schema context overhead by ~56%, while supporting opt-in alias listing via `KONGOR_MCP_INCLUDE_ALIASES=1` (`includeAliases: true`), domain subset filtering via `KONGOR_MCP_TOOLSETS` (`all`, `core`, `ui`, `re`, `server`), and 100% backward-compatible alias execution in `ExecuteToolAsync`.
2. **Test-Only Public Type Burn-Down (`P2.2`, `max_test_only_public_types: 735 -> 158`)**:
   - Tagged `phase17-19-unwired` at the pre-P2 commit, internalized **577** test-only public types across 119 files in `Kongor.Modding`, `NEXUS.CdnBroker`, `NEXUS.VulkanInterop`, `NEXUS.Contracts`, `Kongor.Core`, `KONGOR.MasterServer`, `TRANSMUTANSTEIN.ChatServer`, `DAWNBRINGER.WebPortal.UI`, and `Kongor.Cli` via `<InternalsVisibleTo>`, and ratcheted `max_test_only_public_types` in `data/gate/slop_rules.json` down from `735` to **`158`**.
3. **Core MCP IPC & Handler Empty-Catch Burn-Down (`P2.3`, `src/tools/kongor-cli: 365 -> 239`)**:
   - Replaced **126** silent `catch { }` blocks with structured diagnostic logging (`Log`, `_logger`, `LogDiagnostic`, `Debug.WriteLine`) across `GameIpcBridge.cs`, `CompositeGameIpcBridge.cs`, `NexusMailboxIpcBridge.cs`, `McpServerIpcBridge.cs`, `McpIpcServer.cs`, `ClientOutputRingBuffer.cs`, `LifecycleService.cs`, `EngineTelemetryDiagnostics.cs`, `UiControlHandler.cs`, `KongorOfficialMcpAdapter.cs`, `GameClientManageHandler.Command.cs`, `GameClientManageHandler.Launch.cs`, `GameClientManageHandler.Logs.cs`, and `GameClientManageHandler.Lifecycle.cs`, ratcheting `"src/tools/kongor-cli"` in `data/gate/slop_rules.json` down from `365` to **`239`**.
4. **High-Occurrence `kongor/*` Issue Closure (`P2.4`)**:
   - Verified and closed `MCP-136`, `MCP-138`, `MCP-141`, `MCP-142`, `MCP-149`, and `MCP-215` with deterministic regression tests in `McpToolContractTests.cs`, active pipe-name offline error reporting and `ReloadInterfaces` eval resilience in `ClientEvalLuaHandler.cs`, lazy `DuckDBConnection` initialization in `DuckDbMatchAnalyticsService.cs`, and `status: resolved` + `verified_by:` metadata in `docs/reference/mcp-issues/`.

---

## P3 — Golden Schema Snapshot, Domain Modularization & Zero `[MCP-PROMOTE]` Closure (Implemented)

1. **Golden `tools/list` Snapshot Contract (`P3.1`, [`tests/golden/mcp_tools_list.json`](../../tests/golden/mcp_tools_list.json))**:
   - Locked all 99 canonical MCP tool names, categories, 125 aliases, descriptions, required parameter arrays, and property schemas (including `defaultValue` and `enum` constraints) into a deterministic golden snapshot verified on every test run by `P3_1_GoldenToolsListSnapshot_MatchesLiveMcpToolRegistry` in [`tests/unit/tools/Kongor.Mcp.Tests/McpToolContractTests.cs`](../../tests/unit/tools/Kongor.Mcp.Tests/McpToolContractTests.cs).
2. **Domain Modularization of `McpToolRegistry` (`P3.2`, `5,161 -> 1,409` lines)**:
   - Replaced monolithic tool definitions in [`src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry.cs`](../../src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry.cs) and chronological `McpToolRegistry.Phase3..9.cs` files with 5 domain-focused partial files (`McpToolRegistry.GameLifecycle.cs`, `McpToolRegistry.LuaUi.cs`, `McpToolRegistry.VfsAssets.cs`, `McpToolRegistry.Forensics.cs`, `McpToolRegistry.DedicatedServer.cs`, plus `McpToolRegistry.ControlFabric.cs`), verified by `P3_2_McpToolRegistry_IsModularizedIntoDomainPartialFiles`.
3. **Schema Completion Pass (`P3.3`, `Required` & `Enum` Constraints)**:
   - Populated explicit `Required` arrays across all 99 canonical tools (`Required = []` on 21 zero-arg/defaulted tools and non-empty `Required` arrays on 78 mandatory-arg tools) and explicit `Enum` value lists across 148 finite string parameters (`action`, `mode`, `detail`, `format`, `category`, `condition`, `source`), guarded by `P3_3_CanonicalToolSchemas_MeetRequiredAndEnumCompletionThresholds`.
4. **Closure of Remaining 11 `kongor/*` `[MCP-PROMOTE]` Issues & `protocol-core` / `compel` Empty-Catch Burn-Down (`P3.4`)**:
   - Closed all 11 remaining `kongor/*` `[MCP-PROMOTE]` issues (`MCP-002`, `MCP-003`, `MCP-014`, `MCP-048`, `MCP-051`, `MCP-065`, `MCP-088`, `MCP-119`, `MCP-194`, `MCP-196`, `MCP-210`) with handler guards in [`UiControlHandler.cs`](../../src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs), [`CaptureWindowHandler.cs`](../../src/tools/kongor-cli/Mcp/Handlers/CaptureWindowHandler.cs), [`WindowCapturer.cs`](../../src/tools/kongor-cli/Mcp/Services/WindowCapturer.cs), [`GameServerManageHandler.cs`](../../src/tools/kongor-cli/Mcp/Handlers/GameServerManageHandler.cs), [`WaitForHandler.cs`](../../src/tools/kongor-cli/Mcp/Handlers/WaitForHandler.cs), and [`ClientCommandRemediationService.cs`](../../src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs), deterministic xUnit tests in `McpToolContractTests.cs`, and `status: resolved` + `verified_by:` metadata so **0** `kongor/*` issues remain in `[MCP-PROMOTE]`.
   - Eliminated **55** silent `catch { }` blocks in `src/libs/protocol-core` (`86 -> 31`) and **62** silent `catch { }` blocks in `src/apps/compel` (`86 -> 24`), ratcheting both down in [`data/gate/slop_rules.json`](../../data/gate/slop_rules.json).

---

## P4 — Live MCP Self-Loopback Elimination, Zero-EOF Hot-Reload Supervisor & Alias Hardening (Implemented)

1. **`willowmaker-ipc` In-Process Self-Loopback Prevention & `MCP-217` Pipe Reconnect (`P4.1`)**:
   - [`McpIpcServer`](../../src/tools/kongor-cli/Mcp/Services/McpIpcServer.cs) tracks active pipes hosted in the current process (`IsPipeHostedInCurrentProcess`), preventing [`GameIpcBridge.ConnectAsync`](../../src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs), [`EngineTelemetryDiagnostics.CheckIpcConnectivity`](../../src/tools/kongor-cli/Mcp/Services/EngineTelemetryDiagnostics.cs), and [`GameClientManageHandler`](../../src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.Lifecycle.cs) from connecting `kongor.exe` as a client to its own `willowmaker-ipc` server and poisoning `McpServerIpcBridge.IsConnected`.
   - [`GameIpcBridge`](../../src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs) automatically reconnects and retries once on broken/stale native pipes (`MCP-217`), and [`CompositeGameIpcBridge`](../../src/tools/kongor-cli/Mcp/Services/CompositeGameIpcBridge.cs) fast-fails when `McpServerIpcBridge` has no external client instead of blocking for `3000ms`.
2. **Zero-Downtime Auto-Hot-Reloading Stdio Supervisor (`P4.2`, [`src/tools/kongor/mcp_server.py`](../../src/tools/kongor/mcp_server.py))**:
   - Upgraded `mcp_server.py` into a stdio supervisor proxy that holds the IDE's `sys.stdin` / `sys.stdout` open permanently, checks `kongor.dll` / `kongor.exe` modification timestamps before forwarding requests, stages generation-isolated shadow copies (`pid_<pid>_gen_<n>`), and transparently replays the MCP initialization handshake on hot-swap—preventing both stale shadow binaries and `MCP-134` stdio EOF errors.
3. **`file_path` / `filePath` Parameter Aliases & Automatic Antigravity Schema Sync (`P4.3` & `P4.4`)**:
   - Added `file_path`, `filePath`, and `file` parameter aliases to [`VfsLocateHandler`](../../src/tools/kongor-cli/Mcp/Handlers/VfsLocateHandler.cs) and [`K2LintHandler`](../../src/tools/kongor-cli/Mcp/Handlers/K2LintHandler.cs), auto-refreshed `<appDataDir>\\mcp\\kongor\\*.json` in [`McpCommand`](../../src/tools/kongor-cli/Commands/McpAndToolCommands.cs) whenever `kongor.dll` is newer than the registered schemas, ratcheted `src/tools/kongor-cli` empty catches down to `236`, and locked the contract with `P4_SelfLoopbackPrevention_BrokenPipeReconnect_AndFilePathAliases` in [`McpToolContractTests.cs`](../../tests/unit/tools/Kongor.Mcp.Tests/McpToolContractTests.cs).

