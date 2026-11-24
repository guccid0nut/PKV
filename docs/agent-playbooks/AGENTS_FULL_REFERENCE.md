# Project Kongor: Agent Operational Guidelines ("Kongor-Tail")

All AI agents and subagents operating on Project Kongor must adhere to these directives.

---

## 1. Engineering Philosophy: Pragmatic Minimalism + Heavy Telemetry

### The Decision Ladder (Climb Before Writing)
1. **Scope Pre-Flight Mapping (Before You Write):** Read the task and every file it touches. Map every place the change must reach: callers across all Lua interfaces, native vtables, tests, fixtures, config, and exports. Check what the change could break for players or engine subsystems (null entity crashes, missing textures, VFS precedence collisions).
2. **YAGNI (You Aren't Gonna Need It):** Question speculative requirements. The best code is the code you never wrote.
3. **The Smallest Complete Change:** Be lazy about the solution, never about the change itself. Finish every part the task needs (callers, tests, edge cases, error trapping). Take the first option that fully works:
   - Does it need to exist? Skip features, options, and flexibility nobody asked for.
   - Already in this codebase (a helper, component, service, engine cvar)? Use it the way surrounding code does.
   - Standard library or engine native feature? Use it before writing custom abstractions.
   - An installed dependency? Use it; never add a dependency for a few lines.
   - Otherwise: the minimum code that fully works.
4. **Never Cut Non-Negotiables:** Never cut error handling that prevents native crashes or data loss, security, validation at trust boundaries, extensive telemetry/logging, or anything the user asked for. Between options of equal size, take the one that is robust on edge cases.
5. **No Unrequested Abstractions:** No single-use interfaces, premature factories, or speculative wrappers. Prefer direct, readable implementations with minimal diffs.
6. **Root-Cause Bug Fixing:** Trace callers to the root dispatch function and fix the issue at the source, rather than band-aiding individual call sites.
7. **Documented Shortcuts (Debt Tracking):** Any deliberate temporary shim, mock packet, or provisional hook must carry a code comment: `shortcut: <the limit>, <when to upgrade>`. Run `scripts/qa/audit_debt.ps1` to audit and prevent tech debt rot.

### Mandatory Invariant: Extensive Logging & Debuggability
- **Debuggability > Code Golf:** Never trim or omit logging to make code shorter or diffs smaller.
- **Log aggressively:** Every state transition, packet handler, tool interaction, and fallback branch must have verbose, descriptive log statements. Clutter in the development/debug environment is fully acceptable if it accelerates solving issues.
- **Windows PowerShell Compliance:** Use `Select-String`, `Copy-Item`, `Get-ChildItem` (never Linux `grep`, `copy`, etc.).
- **Instant Log & Dump Discovery with Everything:** ALWAYS use the `everything` MCP server (`everything_search` with `sort: "date-modified-desc"`, `path: <workspace>`, and queries like `ext:log`, `ext:dmp`, or `ext:log|ext:dmp`) to locate the latest client/server logs and crash dumps in milliseconds. NEVER perform slow recursive PowerShell scans (`Get-ChildItem -Recurse`) across the repository or `userdata/` trees for log and dump discovery. Once a dump is discovered, immediately use the `windbg` MCP server (`open_cdb_dump`, `run_cdb_command`) to inspect it.

### Core Disciplines & Guardrails
- **Mandatory IDE-Native File Editing Rule (One-Click Undo Guarantee):** Always use the IDE's native file editing tools (`replace_file_content`, `write_to_file`) to modify codebase files so the user can review diffs and undo changes directly in the IDE UI. Never run Python or shell scripts to batch-modify codebase files without explicitly requesting and receiving user permission first, and only propose batch scripts when genuinely necessary.
- **User Sovereignty ("The Iron Man Suit" Principle):** AI models recommend; the user decides. When an agent or subagent identifies a change that alters the user's stated direction or modifies application/tool source code (e.g. C# projects, native tools, core binaries), **always ask for permission first**. Never act unilaterally and explain afterward.
- **Boil the Ocean, Lakes First (Completeness is Cheap):** The cost of completeness with AI is near-zero. Never choose a 90% shortcut over a 100% complete solution. Implement all edge cases, null checks, error trapping, and logging immediately. Never defer tests or error handlers to a "follow-up".
- **Actionable Error Philosophy ("Errors Are for AI Agents, Not Humans"):** When a tool, script, or test fails, it must never emit raw stack dumps or generic "Failed". Every error message must be actionable, explicitly stating: (1) what failed, and (2) the exact command or script to fix or diagnose it (e.g. `Run scripts/verify_client_lua.ps1 -Path <file>` or `Launch with scripts/launch_screen_practice.ps1`).
- **The Iron Law of Investigation:** No fixes without investigation. Trace data flow, formulate testable hypotheses, and **STOP after 3 failed fixes**. If 3 attempts fail, freeze all code edits and re-triage via telemetry (WinDbg, Frida, Ghidra, logs) before attempting any further changes.
- **Scope Freezing & Edit Boundaries:** When fixing bugs or developing features, file modifications are strictly frozen to the target subsystem or directory (e.g. `games/client/override/`). Collateral edits to unrelated tools, libraries, or server code are strictly forbidden without explicit user permission.
- **Multi-Perspective Planning Pipeline:** Before writing code, run a 3-tier challenge:
  1. *Scope Challenge (Founder/CEO):* Can we solve this with 80% less code? What is the narrowest viable wedge?
  2. *Engineering Review (EM):* Lock in architecture, data flow, failure modes, and test plan.
  3. *DX Review:* Are the CLI commands, options, and error messages intuitive and self-healing?
- **The 5-Phase Executable Spec Protocol:** Before implementing non-trivial features or refactors, execute through 5 phases: (1) Intent & Why, (2) Scope & Non-Goals, (3) Mandatory Code-Reading Gate (**Proof-of-Read Invariant:** must search, read, and quote exact line numbers, variable names, and exit paths from every script/file modified, and explicitly surface any Doc-vs-Code contradictions before drafting), (4) Specification Draft, and (5) Implementation without speculative deviation.
- **Subagent Role Split (`Game Worker` vs. `Offline Worker`), Filesystem Mutex & Anti-Pileup Rule:** When dispatching subagents (`invoke_subagent`), enforce the [`kongor-subagent-orchestration`](../../.agents/skills/kongor-subagent-orchestration/SKILL.md) split: at most **one** `kongor-game-worker` may touch `hon_x64.exe`, `\\.\pipe\kongor-native-client`, or `games/client/override/` at a time (guarded by `scripts/kongor_worker_lock.ps1`), while parallel `kongor-offline-worker` instances (`$env:KONGOR_WORKER_ROLE="kongor-offline-worker"`) perform static Ghidra/VFS/test work and return a risk-ordered live probe queue. Never queue $> 1$ unproven offline batch ahead of live `kongor-game-worker` verification.
- **Singleton Test & Build Execution Mutex (`test-runner` Lock):** No two agents or subagents may run `dotnet build`, `dotnet test`, `pytest`, or `cmake --build` concurrently in the same repository checkout (which collides on MSBuild `obj/`/`bin/` file locks and shared test state). Every build/test command must execute through `scripts/Invoke-KongorTestLock.ps1 -Owner <agent-id> -- <command>` (backed by `scripts/kongor_worker_lock.ps1 -Scope test-runner` at `$env:TEMP\kongor_test_runner.lock`), which automatically queues up to 240s, reaps dead/expired locks, and guarantees release on exit.
- **Cleanroom Privacy, Secret & Link Hygiene (Zero Workstation/PII Leakage & Valid File URIs):**
  - *Committed Markdown Docs (`docs/`, `*.md` in git):* Never commit real passwords, connection-string URIs with embedded credentials, API/COMPEL tokens, personal OS usernames, workstation hostnames, personal emails, hardware serials/MACs, or third-party player handles/IPs in code, tests, scripts, or docs. Always write repo-relative links in Markdown docs (e.g. `[script](../../scripts/launch_screen_practice.ps1)` or `artifacts/captures/verify.png`; never absolute `file:///C:/Users/...` or drive letters) so docs remain portable and clean of workstation PII across developer machines.
  - *Chat UI Responses & Interactive Artifacts:* When generating clickable `file:///` links for the user's IDE in chat messages or artifacts, links MUST be full, valid absolute file URIs with forward slashes pointing to the actual filesystem path (e.g. `file:///c:/Workspaces/ProjectKongor/captures/verify.png`). **NEVER emit headless or truncated relative `file:///` URIs** (such as `file:///captures/...` or `file:///scripts/...`); on Windows, the IDE resolves `file:///path` against the drive root `C:\path` and crashes with "File not found".
  - *Visual Verification Proofs & Image Artifacts:* For verification screenshots or visual assets, always copy them to the conversation artifact directory (`<appDataDir>\brain\<conversation-id>`) and present an interactive artifact (`carousel` / markdown images) so the user can inspect the visual proof directly in the IDE sidecar without relying on external file paths.
- **Destructive Command Guardrails (The Data Loss Firewall):** Never execute destructive shell or git commands without explicit user permission. Strictly block/ask before running: `git reset --hard`, `git checkout .`, `git restore .`, `git push --force` to default branches, recursive deletions (`Remove-Item -Recurse -Force`) on repository roots or untracked directories, and volume commands (`Format-Volume`, `Clear-Disk`).
- **The "Slop-Scan" Rules (Real Fixes vs. Metric Gaming):**
  - *Preserve Full Test Concurrency (Concurrency Exposes Real Bugs):* Keep full test parallelization and concurrency enabled across all test suites. When a test fails, stalls, or flakes under concurrent execution, profile the concurrent run (e.g. TRX per-test timing) and fix the underlying contention, uncached I/O, lock, or race condition that concurrency exposed—never disable parallelization (`DisableParallelization = true`, serial `[Collection]`, or `-maxcpucount:1`) to make a test pass.
  - *Gate Slow Tests (`> 2.0s`) Behind `KONGOR_RUN_MANUAL_TESTS` / `NEXUS_RUN_MANUAL_TESTS`:* Any individual test taking longer than **2.0 seconds** (excluding one-time initial assembly/fixture warm-up tests) must first be investigated for avoidable bottlenecks; if it is genuinely a heavy test, gate it with `if (!NexusTestEnvironment.IsManualTestEnabled) return;` (which checks `KONGOR_RUN_MANUAL_TESTS` / `NEXUS_RUN_MANUAL_TESTS` in [`tests/shared/NexusTestEnvironment.cs`](../../tests/shared/NexusTestEnvironment.cs)) and `[Trait("Category", "Manual")]` so default test runs and `git push` stay fast.
  - *No Empty Catches on Process Kills:* Never swallow exceptions when stopping `hon_x64` or `hon_server`. A swallowed kill failure leaves zombie processes holding file handles on `.s2z` archives.
  - *No Empty Catches on File Operations:* Swallowed deletion/copy errors cause silent VFS archive poisoning.
  - *No Fragile Error String-Matching:* Avoid string-matching error logs (`err.includes("closed")`); engine messages drift across builds. Use typed exception codes and exit statuses.
  - *Fail-Safe Emergency Cleanup:* Emergency shutdown and crash reporting paths must be fail-safe so minidumps and logs are guaranteed to flush.

---

## 2. Mandatory MCP Issue Tracking & First-Time Success Protocol
Detailed in [`.agents/rules/mcp_issues.md`](../../.agents/rules/mcp_issues.md) and indexed in [`docs/reference/mcp-issues/`](../reference/mcp-issues/) (with summary stub at [`docs/MCP_ISSUES.md`](../MCP_ISSUES.md)):
- **Optional External MCP & CLI Tooling Bootstrap (Fresh Cleanroom Clones):** Cleanroom GitHub clones ship only source code + the `native/deps/rmlui` (`MIT`) submodule. If optional local tools (`clangd`, `texconv`, `everything` / `es.exe`, `ffmpeg`, `proximo`, `K2-Blender-master`, `voicebox`) are not yet installed on a developer workstation, follow [`docs/getting-started/optional-agent-tools.md`](../getting-started/optional-agent-tools.md) and run `scripts/Install-OptionalAgentTools.ps1 -WhatIf` (asking user permission before installing).
- **Automatic C# Pre-Flight & Self-Healing (`0 ms`, Default-ON):** `McpToolRegistry` enforces automatic pre-flight liveness checks, `MatchIssueForError` pitfall enrichment, and structured `remediation_call` payloads by default (`skip_preflight: true` to opt out). Manual pre-flight PowerShell searches (`scripts/kongor_mcp_issues.ps1 -Action search`) are retired and no longer required before invoking MCP tools.
- **Record (`new`) or Increment (`bump`):** Whenever **ANY** MCP command or tool call fails, times out, or behaves unexpectedly, immediately record it via `scripts/kongor_mcp_issues.ps1`:
  - If the failure matches an existing `MCP-NNN` issue, increment its counter via `scripts/kongor_mcp_issues.ps1 -Action bump -Id MCP-NNN`.
  - If the root cause is new, scaffold `docs/reference/mcp-issues/MCP-NNN-<slug>.md` from [`docs/reference/mcp-issues/TEMPLATE.md`](../reference/mcp-issues/TEMPLATE.md) via `scripts/kongor_mcp_issues.ps1 -Action new` and run `-Action rebuild-index` / `-Action check`.
- **Mandatory 35-Second Liveness & Anti-Stall Check:** Actively check on all executing MCP commands, async operations, and background tasks at least every **35 seconds**. Never let an MCP operation run unchecked without verifying status or progress; if 35 seconds elapse with no progress, inspect logs, kill the stalled task, record or bump the failure via `scripts/kongor_mcp_issues.ps1`, and apply self-healing.


---

## 3. Mandatory Testing, Hot-Reload & Runtime Verification Protocol
Detailed in [`.agents/rules/testing_and_runtime_verification.md`](../../.agents/rules/testing_and_runtime_verification.md):
- **Iterate via `override/` & Hot-Reload First:** Place candidate modifications into `games/client/override/<path>` and hot-reload live (`kongor_hot_reload`, console `ReloadInterfaces` / `ReloadInterface <name>` / `ReloadRes` / `ReloadSelected`, `UICmd("ReloadFile('<vpath>')")`, or Lua eval via `kongor_client_eval_lua`) to test immediately without client restarts.
- **Override Preservation & On-Demand Non-Destructive Injection:**
  - `games/client/override/` is the active development workspace and is tracked by git. Changes in `override/` can be committed and pushed directly to remote at any time without requiring archive injection.
  - Git push and git commit **NEVER** run archive injection or evict files from `override/`.
  - When archive injection is explicitly performed (`scripts/vfs/inject_override_to_s2z.ps1`), it uses the 5-tier `.bak` rotation protocol and **NEVER** deletes files. Injection can be reverted at any time with `scripts/vfs/revert_injected_override.ps1`.
- **Live Runtime Triangulation (Frida, Ghidra, WinDbg, Logs):**
  - Attach **Frida** while `hon_x64.exe` is running to hook native functions, trace vtables, intercept Lua calls, and inspect registers.
  - Query **Ghidra** MCP against `ProjectKongor.rep` (7 core binaries: `k2_x64.dll`, `cgame_x64.dll`, etc.) to decompile crash offsets (RVAs), analyze symbols, and inspect memory structures.
  - Query **Everything** (`everything_search`) to locate the newest `*.log` files and `*.dmp` minidumps instantly with sub-second response times.
  - Analyze Crash Dumps with **WinDbg** (`windbg` MCP): Open crash dumps using `open_cdb_dump` and execute diagnostic commands (`!analyze -v`, `.exr -1`, `k`, `r`, `lm vm`) to extract the exact faulting instruction, exception record, registers, and module offsets before correlating with Ghidra.
  - Follow and triage logs aggressively (`console.log`, `client_lua_debug.log`, `diag_*.log`, `scripts/triage_client_logs.ps1`).

- **Non-Browser & CLI QA Exploration:** When verifying CLI tools, bot choreographies, IPC Named Pipe commands, or headless engine fixtures:
  1. Declare the target fixture, command, and boundaries upfront.
  2. Log intermediate discovery probes during exploration.
  3. Prove bugs with a reproducible failing test before writing the fix, then prove the fix passes afterward.
- **Pre-Flight Syntax & Lint Checks:** Run `scripts/verify_client_lua.ps1` and `scripts/verify_xml_syntax.ps1` before hot-reloading. `scripts/vfs/inject_override_to_s2z.ps1` enforces an automated pre-injection syntax guard, rejecting injection if bytecode or XML errors are present.
- **Pre-Injection Secret Scrubbing Guard:** `scripts/vfs/inject_override_to_s2z.ps1` automatically blocks injection if plaintext passwords, bearer tokens, or API keys are detected in candidate files.
- **Frame Budget & Hot-Loop Telemetry:** Keep individual Lua UI update handlers and tick callbacks under $\le 1.0\text{ ms}$ to preserve the $144\text{ FPS} / 6.9\text{ ms}$ main-thread frame budget; emit warnings on any tick hitch exceeding $5\text{ ms}$.
- **Mandatory VFS Precedence Verification:** Before modifying or overriding any asset, inspect the VFS resolution order (`kongor_vfs_resolve`, `kongor_vfs_locate`, or `scripts/vfs/read_archive_entry.ps1`) to confirm which `.s2z` or folder takes precedence. Never assume an asset path without verifying against K2 mounting hierarchy (`override` > `base` > `game` > `ClientPackageFiles` > `shared` > `KONGOR/*`).
- **Perceptual Visual Baseline Diffing Protocol (Golden Screens):** Before UI/store/visual modifications, capture a baseline screenshot (`kongor_capture_window` / `kongor_client_capture_preview`). After hot-reloading in `override/`, diff against the baseline (`kongor_ui_visual_diff` / `kongor_assert_visual`) to ensure 0 layout distortions, clipping, or visual regressions before archive injection.
- **The Post-Reload / Post-Deploy Canary Loop:** After hot-reloading a UI interface or injecting an `.s2z` archive, run a 30-to-60 second canary observation loop monitoring `console.log` and `client_lua_debug.log` to catch delayed Lua errors, texture load faults, or memory spikes during the settle window before declaring victory.
- **Shared Code Deslopping & Deduplication:** Periodically sweep for duplicate utility functions (color formatting, string parsing, widget anchor math) copied across Lua files (`main_v2.lua`, `store2.lua`, `regions.lua`). Consolidate into existing core libraries (`ClientPackageFiles/ui/scripts/newui/`) without creating premature or speculative wrappers.
- **Isolated Practice Mode & Visual Validation:** Use `scripts/launch_screen_practice.ps1` or `kongor_client_practice` for fast zero-friction testing; verify layout via `kongor_capture_window` / `kongor_assert_visual`.
- **Mandatory Visual Proof Verification & Semantic Truth Invariant ("Real Proof or No Proof"):**
  - *The False-Proof Trap Strictly Prohibited:* Blindly executing a window capture (`kongor_capture_window`) while the client is sitting on the pre-login screen (`gamePhase 0: Login`), at a "Login Failed: Connection failed" modal, or on a blank loading screen and presenting it as "proof" of an in-game HUD feature, Store catalog item, or gameplay mechanic is strictly prohibited as false proof.
  - *Pre-Capture State & Context Assertion:* Before capturing any proof screenshot, the agent MUST explicitly query and assert the active engine state via `kongor_game_client_manage action: 'status'` or `kongor_client_eval_lua`:
    1. *In-Game Features (HUD, Heartbeat Mod, Hero Spells, Range Rings):* The client must be in an active match or practice mode (`gamePhase == 5` / `isActiveMatch == true`), the relevant hero must be spawned in-world, and the specific target widget or visual state (e.g. low HP for heartbeat pulse) must be actively triggered and visible (`widget:IsVisible() == true`).
    2. *Front-End / Store / Vault / Codex:* The Aspire cluster (`master-server:55555`) must be running, the client must be authenticated past the login screen (`gamePhase == 1` / `GetWidget('main_top_bar'):IsVisible() == true`), and the target container or turntable (`store_container2`, `player_vault`, etc.) must be visibly displayed and focused.
    3. *Modals (Cinematic Theater, Options, Dialogs):* The modal widget (`cinematic_theater_modal`, `game_options_window`) must be visible (`modal:IsVisible() == true`) and rendering its content in front.
  - *Mandatory Visual Content Inspection Gate:* Immediately after capturing any proof screenshot, the capturing agent MUST inspect the image:
    1. *Login Screen Rejection:* If the image displays the Empath login pedestal, "HEROES OF NEWERTH" header, or "Login Failed" dialog, the proof is automatically **DISQUALIFIED (REJECT)**. The agent must start required background services (e.g. `kongor_server_manage action: 'start' target: 'cluster'`) or launch into practice mode (`scripts/launch_screen_practice.ps1`) to reach the genuine screen.
    2. *Blank/Loading Screen Rejection:* If the screen is black, blank, or in transition, the proof is automatically **DISQUALIFIED (REJECT)**.
    3. *Target Semantic Check:* The agent must verify and describe the specific visual feature present in the image (e.g. "Abyssal Bubbles model loaded on turntable, cyan crystal staff glow billboard at bone RightHand visible, Store Alt Avatars grid showing Alt16 selected").
  - *Automated Proof Inspection (`scripts/verify_proof_screenshot.py`):* Run `python scripts/verify_proof_screenshot.py --image <path> --expected <feature_type>` to programmatically verify that the screenshot is valid, non-blank, and not stuck on the login screen before citing it in commits, artifacts, or responses.
  - *Artifact Presentation & Delivery Gate:* Never link raw capture files via headless relative `file:///` URIs in chat messages. Always copy verification screenshots to `<appDataDir>\brain\<conversation-id>` and present them inside an interactive markdown artifact (`carousel` or embedded markdown images) so the user can inspect them directly within the IDE sidecar, while keeping any direct file links fully qualified with the absolute workspace path (`file:///c:/...`).
- **Durable Session Ledger:** Active task state, hypotheses, scope freeze boundaries, and telemetry findings are tracked in [`docs/ACTIVE_SESSION_STATE.md`](../ACTIVE_SESSION_STATE.md) for cross-turn and cross-agent continuity.
- **The Reconstruction Obligation Ledger Protocol:** When reverse-engineering native K2 functions (`k2_x64.dll`, `cgame_x64.dll`, `game_x64.dll`) or network packets, author/update an Evidence Dossier v2 in `artifacts/evidence/`. Extract an explicit `obligations` ledger (null checks, boundary clamps, bitmask guards, return codes) and verify via `scripts/verify_evidence_obligations.ps1`. Cleanroom code must tag satisfied items (`// satisfies: OBL-XX`) and provide matching unit/integration tests before injecting or landing changes.
- **Static <-> Dynamic Triangulation Protocol (Contradiction Detection):** Never trust static Ghidra decompilation alone for virtual method dispatch (vtables) or opaque struct offsets (`+0x88`). Mark unresolved fields in `limitations_and_unknowns` and dynamically triangulate via Frida (`scripts/re/triangulate_rva.js`) or WinDbg before modifying engine code. Low-risk unneeded fields must carry a documented `waived` status with a documented rationale; blind guesses are strictly prohibited.
- **Value-Weighted Testing Gate (The Test Value Bar):** Reject hollow "★ tests" (trivial existence checks). Every new or modified test must satisfy the 3-question gate: (1) `protects` (what public contract, memory boundary, or engine invariant is guarded), (2) `fails_when` (under what realistic defect does it actually fail), and (3) `seam` (tests real boundaries like `luac51`, `XmlDocument`, or live client IPC rather than artificial mocks).
- **Finite Probe Budgets & Hard Execution Deadlines:** Exploratory QA, bot choreography probes, and crash investigations must operate under a strict probe budget (max 10 probes) and a hard execution deadline (e.g. 5 minutes). At the limit, halt exploration cleanly, write `exploration-NNN.json` telemetry notes, and present concrete findings.
- **Strict 32-bit Zip Offset Limits:** Keep every `.s2z` under 1.5 GB and ensure `allowZip64 = False` to prevent signed 32-bit integer overflow crashes in `k2_x64.dll`.
- **Native Hook Main-Thread & SEH Invariant (`version.dll` / `NativeGuard`):**
  - External overlays (e.g. OBS `graphics-hook64.dll`) inject background threads into `hon_x64.exe` and pump `WndProc`. Any window procedure (`KongorSubclassWndProc`) or IPC handler in `native/proxies/native_guard/` must verify `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` and re-post via `PostMessage` if called off the main window thread.
  - Never call `CConsole::Execute` synchronously from an IPC or hook callback; queue commands to the main-thread ring via `CConsole::AddCmdBuffer`.
  - Never poll files per-frame in Lua (`loadfile`), which exhausts Lua's allocator in `luaV_concat` (`k2_x64.dll + 0xCB6477`); use CVar change-signals (`tao_req`) and wrap native vtable/trampoline hooks in `__try / __except` (SEH).
- **Hermetic Scratch Isolation & Hard-Link Protection for AssetDB / Batch Tools:**
  - Never run `tools/assetdb/sync_record.py`, `pytest tools/assetdb`, or `scripts/prebake-assets.ps1` without an explicit isolated scratch directory (`--repo-root <temp_dir>` or `-OutputDir <scratch_dir>`).
  - Never modify `games/client/game/textures.s2z`, `resources94.s2z`, or hard-linked client/server archives (`resources0.s2z`, `resources5.s2z`, `resources98.s2z`) in-place without checking `fsutil hardlink list` and receiving explicit user approval.
- **K2 UI & Rendering Footguns (`textures.s2z` Shadowing, `<modelpanel>` Stutter & DXVK v3.0.2 Architecture):**
  - *Vulkan & DXVK v3.0.2 Pipeline (`docs/explanation/vulkan-and-dxvk3-architecture.md`):* `hon_x64.exe` runs `games/client/vid_vulkan_x64.dll` (Spy-and-Forward 92-slot `VidDriver`), which delegates D3D9 scene rendering to `games/client/vid_d3d9_x64.dll`, which in turn loads `games/client/d3d9.dll` (**DXVK v3.0.2**, configured via `games/client/dxvk.conf` with `d3d9.maxFrameRate = 144` and `dxvk.enableGraphicsPipelineLibrary = true` and logged to `games/client/hon_x64_d3d9.log`) translating all D3D9 calls to native Vulkan 1.3 (`vulkan-1.dll` + `VkLayer_hon_replay_capture.dll`). Never add redundant CPU vertex/index staging buffers to `vid_vulkan_x64.dll`.
  - *Unshadowable UI Textures:* `CResourceManager::RegisterTexture` gives legacy entries in `games/client/game/textures.s2z` (`00000000/ui/...`) special precedence that can silently shadow identically named overrides in `games/client/override/`. When replacing a UI texture that exists in `textures.s2z`, use a versioned basename (e.g. `tab38_*.dds`) and update the `.package`/`.interface` reference.
  - *`<modelpanel>` Animation Initialization:* Setting `anim="idle"` as a static XML attribute on a `<modelpanel>` causes `CModelPanel::Frame` (`k2_x64.dll + 0x951020`) to invoke `CSkeleton::StartAnim` every tick, resetting `StartTime` to `Host.m_uiTime` ($\Delta t \equiv 0$) and freezing/stuttering the model. Always initialize animations once via `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`, and set `scenebuffer="false"` unless an offscreen render-target blit is strictly required.
- **Inner-Loop Gate & Cross-Tree Parity (`scripts/gate_check.ps1`):**
  - Before declaring any task complete, execute `scripts/gate_check.ps1` (sub-5s 6-tier verification: Tier 1 BannedSymbols + PowerShell AST + Field-Note KB & MCP-Issue KB drift, Tier 2 Lua modernization contracts, Tier 3 XML/interface schema lint, Tier 4 `version.dll` / `libk2_native_guard.so` + `vfs_mounts.json`, Tier 5 Cross-Tree Asset Parity across `override/` $\leftrightarrow$ `game/`, `verify_lua/`, `server/`, and `ClientPackageFiles/`, and Tier 6 Duplicate-Type & Reachability Ratchet), run `scripts/test_gate_mutants.ps1` when modifying gate scripts, and run `scripts/re/test_metagen_parity.ps1` whenever `data/schemas/protocol.json` or shared memory contracts change.

---

## 4. Documentation Architecture: The Diataxis Framework
Organize all documentation under `docs/` strictly into the 4 Diataxis quadrants to prevent knowledge clutter:
1. **Tutorials (Learning-oriented):** Step-by-step beginner guides (e.g. *First Time Setting Up Kongor Practice Mode*).
2. **How-To Guides (Problem/Task-oriented):** Practical recipes for specific goals (e.g. *How to Hook a Packet Handler with Frida*).
3. **Reference (Information-oriented):** Accurate technical specs and tables (e.g. *CVar Catalog, Entity XML Schema, Ghidra RVA Map*).
4. **Explanation (Understanding/Architecture-oriented):** Deep-dive conceptual analysis (e.g. *K2 VFS Archive Mounting Precedence & 32-bit Limits*).

---

## 5. Curated Active Agent Skills
The following curated engineering skills are installed under [`.agents/skills/`](../../.agents/skills/) to reinforce our development workflow:

### Systems Engineering & Reliability (adapted from `addyosmani/agent-skills` & `gamedev-skills/awesome-gamedev-agent-skills`)
1. [`observability-and-instrumentation`](../../.agents/skills/observability-and-instrumentation/SKILL.md): Structured logging, correlation IDs, and event-based telemetry to support aggressive diagnostic visibility.
2. [`debugging-and-error-recovery`](../../.agents/skills/debugging-and-error-recovery/SKILL.md): Systematic root-cause isolation, hypothesis formulation, and non-destructive reproduction for C++/Vulkan/K2 crashes.
3. [`doubt-driven-development`](../../.agents/skills/doubt-driven-development/SKILL.md): Rigorous sanity verification and boundary checks before landing modifications.
4. [`performance-optimization`](../../.agents/skills/performance-optimization/SKILL.md): Enforces "measure and profile first" across K2 client loops (144 FPS / $\le 1.0\text{ ms}$ Lua), D3D9/DXVK submit queues, and .NET/PostgreSQL services.

### Architecture & Agent Continuity (from `mattpocock/skills`)
5. [`handoff`](../../.agents/skills/handoff/SKILL.md): Compacts session state into clean, durable handoff documents for cross-agent continuity.
6. [`codebase-design`](../../.agents/skills/codebase-design/SKILL.md): Enforces "deep modules" (simple interface, deep implementation) and clean seams (avoids shallow wrappers).
7. [`research`](../../.agents/skills/research/SKILL.md): Investigates primary sources (engine headers, disassembly, specs) and records findings into structured notes.
8. [`writing-for-agents`](../../.agents/skills/writing-for-agents/SKILL.md): Standards for writing unambiguous prompts, briefings, and rules for subagents.
9. [`prototype`](../../.agents/skills/prototype/SKILL.md): Fast, isolated scratch prototypes that test hypotheses without contaminating production code.
10. [`domain-modeling`](../../.agents/skills/domain-modeling/SKILL.md): Builds and maintains a precise domain model and `GLOSSARY.md` for reverse engineering and K2 terminology.

### Quality Review, Crash Triage & Technical Debt (Project Kongor Native)
11. [`kongor-review`](../../.agents/skills/kongor-review/SKILL.md): 4-part actionable review of candidate diffs/overrides verifying correctness, crash safety, heavy telemetry, Lua frame budget ($\le 1.0\text{ ms}$), and lean code.
12. [`kongor-debt`](../../.agents/skills/kongor-debt/SKILL.md): Scans and audits deliberate `shortcut:` comments into a structured technical debt ledger using `scripts/qa/audit_debt.ps1`.
13. [`kongor-crash-triage`](../../.agents/skills/kongor-crash-triage/SKILL.md): Deterministic 4-step crash-to-fix playbook (Everything dump discovery $\to$ WinDbg `!analyze -v` / `~* k` $\to$ Ghidra RVA decompilation $\to$ thread-safety & ledger verification).
14. [`kongor-gate`](../../.agents/skills/kongor-gate/SKILL.md): Pre-completion inner-loop verification gate running sub-5s 6-tier `scripts/gate_check.ps1`, hermetic `scripts/test_gate_mutants.ps1`, `scripts/re/test_metagen_parity.ps1`, Field-Note KB promotion, and K2 UI/native safety checks.
15. [`kongor-subagent-orchestration`](../../.agents/skills/kongor-subagent-orchestration/SKILL.md): Singleton `kongor-game-worker` vs. parallel `kongor-offline-worker` split, filesystem mutex (`scripts/kongor_worker_lock.ps1`), 1-batch anti-pileup rule, and cold-reader subagent briefing protocol.

---

## 6. Builder-to-Builder Communication Voice
- **Direct, concrete, builder-to-builder:** Name the file, function, command line, RVA, and user-visible impact.
- **Action-Oriented:** Short paragraphs; always end with concrete next actions or choices.
- **No Fluff:** Eliminate corporate pleasantries, filler phrases, and generic AI vocabulary. Focus on code, telemetry, and system behavior.





