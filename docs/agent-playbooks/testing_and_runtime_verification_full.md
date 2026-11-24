# Rule: Testing, Hot-Reload & Runtime Verification Protocol

This rule is active for all agents and developers operating in Project Kongor.

---

## 🎯 Core Directive & Invariants

Fast iteration, rock-solid stability, and pristine VFS hygiene are the cornerstones of Project Kongor development. All testing must follow these mandatory rules:

1. **Iterate via `override/` and Hot-Reload First:**
   - Place active file changes into `games/client/override/` (matching the exact K2 VFS pathing, e.g. `games/client/override/ui/scripts/store2.lua`).
   - Trigger live hot-reloads (`kongor_hot_reload`, console `ReloadInterfaces` / `ReloadInterface <name>` / `ReloadRes` / `ReloadSelected`, `UICmd("ReloadFile('<vpath>')")`, or Lua eval) while the game client is running to test changes instantly without restarts.
   - Do **NOT** test unverified modifications directly inside production `.s2z` archives.

2. **Strict Override Folder Hygiene:**
   - The `games/client/override/` folder MUST contain **ONLY working files**.
   - Because `override/` sits at index 0 of the engine mount list (`-mod override;base;game...`), any broken, unverified, or abandoned file in `override/` will shadow archive files and poison subsequent runs across the entire team.
   - Never leave broken experiments, scratch test files, or uncommitted prototypes in `override/`.

3. **On-Demand Non-Destructive Injection & Zero Deletion:**
   - `games/client/override/` is tracked by git and files can be committed and pushed directly to remote at any time without forcing injection into `.s2z`.
   - Git push and git commit **NEVER** run archive injection or evict files from `override/`.
   - When archive injection is explicitly performed (`scripts/vfs/inject_override_to_s2z.ps1`), it uses the 5-tier `.bak` rotation protocol and **NEVER** deletes files. Injection can be reverted at any time with `scripts/vfs/revert_injected_override.ps1`.

4. **Live Runtime Instrumentation (Frida, Ghidra & Heavy Logging):**
   - Use **Frida** while the client (`hon_x64.exe`) is running to hook native functions, trace vtables, intercept Lua calls, inspect registers/arguments, and catch faulting conditions live.
   - Use **Ghidra** against the pre-baked project (`src/tools/ghidra/projects/ProjectKongor.rep`) to decompile native functions, resolve crash offsets (RVAs), analyze symbols, and inspect binary structures across all 7 core engine binaries (`k2_x64.dll`, `cgame_x64.dll`, `game_x64.dll`, `vid_*.dll`, `hon_x64.exe`).
   - Monitor and correlate logs aggressively (`console.log`, `games/client/override/logs/client_lua_debug.log`, `diag_*.log`, and `scripts/triage_client_logs.ps1`).

---

## 🔄 The 5-Phase Testing Lifecycle

```
┌────────────────────────────────────────────────────────────────────────┐
│                      PROJECT KONGOR TESTING LOOP                       │
│                                                                        │
│   [1. Edit in override/]                                               │
│            │                                                           │
│            ▼                                                           │
│   [2. Pre-flight Lint] ──(fail)──► Fix Syntax First                   │
│            │ (pass)                                                    │
│            ▼                                                           │
│   [3. Hot-Reload in Live Client]                                       │
│            │                                                           │
│            ├─► Frida: Hook & trace live vtable / Lua calls             │
│            ├─► Ghidra: Decompile RVA / check memory structures         │
│            └─► Logs: Follow client_lua_debug.log & console.log        │
│            │                                                           │
│            ▼                                                           │
│   [4. Verification Gate: Working Perfectly?]                           │
│            │                                                           │
│            ├─► NO  ──► Fix in override/ & re-hotreload                 │
│            │                                                           │
│            └─► YES                                                     │
│                 │                                                      │
│                 ▼                                                      │
│   [5. Run gate_check.ps1: Commit in override/ OR On-Demand .s2z Bake]  │
│            │                                                           │
│            ▼                                                           │
│   (5-tier .bak rotation; override/ stays 100% clean & verified)        │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 1. Hot-Reload & Fast In-Game Testing

### Directory Mapping
The K2 engine resolves files through the VFS mount order:
`-mod override;base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions`

Any file in `games/client/override/` overrides the same relative path inside any `.s2z` archive:

| Target Archive Path | Local Override Path |
|---|---|
| `ui/scripts/store2.lua` | `games/client/override/ui/scripts/store2.lua` |
| `ui/fe2/main_v2.interface` | `games/client/override/ui/fe2/main_v2.interface` |
| `heroes/pyromancer/hero.entity` | `games/client/override/heroes/pyromancer/hero.entity` |
| `heroes/bubbles/alt16/model_store_l.mdf` | `games/client/override/heroes/bubbles/alt16/model_store_l.mdf` |

### Triggering Hot-Reload
While `hon_x64.exe` is running, trigger hot-reload using any of the following:

- **Via TAO MCP Tool:**
  ```json
  { "name": "kongor_hot_reload", "arguments": { "action": "reload", "target": "ui" } }
  ```
  Targets: `"ui"` (reloads UI interfaces and scripts), `"entity"` (reloads entity definitions/MDFs), `"all"` (reloads both).

- **Via Lua Evaluation (`kongor_client_eval_lua`):**
  ```json
  { "name": "kongor_client_eval_lua", "arguments": { "code": "Cmd('ReloadInterfaces')" } }
  ```

- **Via Client In-Game Console:**
  Press `` ` `` or `Ctrl+F8` to open the K2 console and execute:
  - `ReloadInterfaces` — reloads all UI interfaces and executes initializers.
  - `ReloadInterface <name>` — reloads a specific UI interface (e.g. `fe2`).
  - `ReloadRes` — reloads modified K2 resources (`k2_x64.dll + 0xf50740`; enable `host_dynamicResReload true` for automatic hot-reload).
  - `ReloadSelected` — reloads selected unit/entity definitions in-game (`cgame_x64.dll + 0x263eb8`), alongside `ReloadModels`, `ReloadMaterials`, and `ReloadTextures`.
  - `UICmd("ReloadFile('<vpath>')")` — reloads a single UI file via Lua `UICmd` (`k2_x64.dll + 0xf65030`).

---

## 2. Live Runtime Instrumentation: Frida, Ghidra & Logging

### A. Frida Live Process Hooking (`hon_x64.exe`)
When testing complex logic, state transitions, or investigating native crashes while the client runs:

1. **Attach to the live process:**
   Use the `frida` MCP server or Frida CLI:
   - Tool: `attach_to_process` with target `"hon_x64.exe"`.
   - Tool: `execute_in_session` to run JavaScript hooks.

2. **Key Frida capabilities in Kongor:**
   - **Trace VFS File Resolutions:** Hook `k2_x64.dll` file manager calls to confirm whether the engine loaded a file from `override/` or from `.s2z`.
   - **Monitor Lua State:** Hook `lua_pcall` / `lua_call` to log Lua errors, function calls, and stack arguments that would otherwise be suppressed.
   - **Hook C++ Vtables & Handlers:** Intercept client packet handlers, render calls, or inventory update callbacks to observe real-time parameter values.
   - **Trap Unhandled Exceptions:** Intercept structured exceptions (SEH) before the process terminates, dumping register context (`RAX`, `RCX`, `RIP`).

### B. Ghidra Decompilation & Symbol Triangulation
When an engine crash occurs, or when reverse-engineering undocumented engine behavior:

1. **Pre-Baked Repository:**
   The pre-analyzed program database is ready at `src/tools/ghidra/projects/ProjectKongor.rep` containing all 7 core binaries:
   - `k2_x64.dll` (Engine core, VFS, memory management, CVars)
   - `cgame_x64.dll` (Client game UI, widgets, entity proxies)
   - `game_x64.dll` (Game logic, state machine, packet parser)
   - `game_shared_x64.dll` (Shared gameplay definitions, physics)
   - `vid_d3d9_x64.dll` / `vid_gl2_x64.dll` (Graphics renderers)
   - `hon_x64.exe` (Entry point launcher)

2. **Common Ghidra MCP Workflow:**
   - **Decompile crash offset:** When a crash log reports `cgame_x64.dll+0x184a20`:
     ```json
     { "name": "decompile_function", "arguments": { "binary_name": "cgame_x64.dll", "name_or_address": "0x180184a20" } }
     ```
   - **Search symbols & xrefs:**
     ```json
     { "name": "search_symbols_by_name", "arguments": { "binary_name": "k2_x64.dll", "query": "CFileManager" } }
     ```

### C. Logging & Telemetry Triage
Log clutter is welcomed if it speeds up issue resolution. Follow these streams:

1. **Client Console Log:** `games/client/console.log` (captures all engine prints, warnings, and unhandled errors).
2. **Client Lua Debug Log:** `games/client/override/logs/client_lua_debug.log` (dedicated high-cadence Lua logging).
3. **Crash Dumps & Diag Logs:** `games/client/userdata/` or root `userdata/` (e.g. `diag_*.txt` and minidumps).
4. **Real-Time Triage Utility:**
   ```powershell
   powershell -File scripts/triage_client_logs.ps1 -Follow -ErrorsOnly
   ```
   Filter specifically by hero or channel:
   ```powershell
   powershell -File scripts/triage_client_logs.ps1 -Hero Hero_Pyromancer -Tail 100
   ```

#### ⚡ Instant Log & Crash Dump Discovery with Everything
**Mandatory Rule:** NEVER run slow recursive filesystem traversals (`Get-ChildItem -Recurse`) across the repository or `userdata/` trees to find recent logs or crash dumps. Instead, ALWAYS use the **Everything** MCP server (`everything_search`), which searches the NTFS index in milliseconds.

* **Find the Latest Game Client & Server Logs:**
  ```json
  {
    "name": "everything_search",
    "arguments": {
      "query": "ext:log",
      "path": "<workspace_root>",
      "sort": "date-modified-desc",
      "max_results": 10
    }
  }
  ```

* **Find the Latest Crash Dumps (`*.dmp`):**
  ```json
  {
    "name": "everything_search",
    "arguments": {
      "query": "ext:dmp",
      "path": "<workspace_root>",
      "sort": "date-modified-desc",
      "max_results": 10
    }
  }
  ```

* **Find Both Logs & Dumps Combined:**
  ```json
  {
    "name": "everything_search",
    "arguments": {
      "query": "ext:log|ext:dmp",
      "path": "<workspace_root>",
      "sort": "date-modified-desc",
      "max_results": 10
    }
  }
  ```

* **Find Recent K2 Diagnostics & Engine Text Dumps:**
  ```json
  {
    "name": "everything_search",
    "arguments": {
      "query": "diag_*.txt|console.log|client_lua_debug.log",
      "sort": "date-modified-desc",
      "max_results": 5
    }
  }
  ```

### D. Crash Dump Analysis with WinDbg / CDB (`windbg` MCP)
When a crash dump (`*.dmp`) is located via Everything, **always use the `windbg` MCP server** to open the minidump, triage the failure bucket, and inspect the stack trace before attempting to patch:

1. **Open the Dump File:**
   Use `open_cdb_dump` with the path discovered by Everything:
   ```json
   {
     "name": "open_cdb_dump",
     "arguments": {
       "dump_path": "<workspace_root>\\games\\client\\userdata\\crash_example.dmp"
     }
   }
   ```

2. **Execute Diagnostic CDB Commands (`run_cdb_command`):**
   - **Full Crash Triage (`!analyze -v`):**
     Automated crash analysis identifying the faulting module, exception code (e.g. `0xC0000005` access violation), faulting IP, and failure bucket:
     ```json
     { "name": "run_cdb_command", "arguments": { "command": "!analyze -v" } }
     ```
   - **Exception Record (`.exr -1`):**
     Displays the recorded exception record, including whether it was a read or write violation and the exact target memory address:
     ```json
     { "name": "run_cdb_command", "arguments": { "command": ".exr -1" } }
     ```
   - **Call Stack Backtrace (`k`, `kn`, `kp`):**
     Prints the stack frames with source line numbers, frame offsets, and parameters:
     ```json
     { "name": "run_cdb_command", "arguments": { "command": "kn" } }
     ```
   - **Register State (`r`):**
     Displays general-purpose registers (`rax`, `rcx`, `rdx`, `rbx`, `rsp`, `rbp`, `rsi`, `rdi`, `rip`, etc.):
     ```json
     { "name": "run_cdb_command", "arguments": { "command": "r" } }
     ```
   - **Module Verification (`lm vm <module>`):**
     Inspects module base address, size, timestamp, and checksum (e.g. `lm vm k2_x64` or `lm vm cgame_x64`):
     ```json
     { "name": "run_cdb_command", "arguments": { "command": "lm vm cgame_x64" } }
     ```

3. **Bridge from WinDbg to Ghidra:**
   Calculate the Relative Virtual Address (RVA):
   $$\text{RVA} = \text{FaultingIP} - \text{ModuleBaseAddress}$$
   Feed the resulting RVA into Ghidra's `decompile_function` (`ProjectKongor.rep`) to see the exact C++ decompilation of the failing function.

4. **Close Session:**
   Always run `close_cdb_session` when analysis is finished to release debugger file handles.

---


## 3. Strict Override Hygiene & The "Working Perfectly" Gate

### What "Working Perfectly" Means
A change is considered **working perfectly** ONLY when:
- **Zero Errors:** No Lua errors, unhandled exceptions, missing asset warnings, or crash guard triggers appear in `client_lua_debug.log` or `console.log`.
- **Behavioral Verification:** The intended feature, fix, or visual asset behaves as designed in-game (verified via visual inspection, practice mode, or automated assertion).
- **No Regressions:** Adjacent screens, hero states, and inventory/store tabs continue functioning without degradation.

### The Injection & Eviction Protocol
Once a file passes the verification gate:

1. **Automated Pre-Injection Syntax Guard (`scripts/vfs/inject_override_to_s2z.ps1`):**
   - The injection utility executes `scripts/verify_client_lua.ps1` (for `.lua` files) and `scripts/verify_xml_syntax.ps1` (for `.xml`, `.interface`, `.package`, `.mdf`, `.effect` files) *before* touching any archive.
   - If any syntax error or bytecode compilation failure is detected, injection aborts immediately with an error, preventing archive corruption or poisoning.

2. **Inject into Target `.s2z`:**
   - UI scripts, store fixes, XML packages, shaders $\to$ `games/client/game/resources99.s2z`
   - Entities, items, triggers, bots, NPCs $\to$ `games/client/game/resources98.s2z`
   - Hero-specific assets $\to$ `resources94.s2z` through `resources97.s2z` (by alphabetical partition)
   - Use the atomic injection utility:
     ```powershell
     powershell -File scripts/vfs/inject_override_to_s2z.ps1 -RelativePath "<relative_asset_path>" -TargetArchive "<target_archive>.s2z" # Note: Do not inject or evict store Lua; override must keep winning (plan v4 §3.9)
     ```

3. **Non-Destructive Backup Rotation:**
   - The injection utility automatically rotates the override file in-place through up to 5 backup generations: `[file.ext] -> [file.ext.bak] -> [file.ext.bak_old] -> ...`
   - Files are **NEVER** deleted. If an injection needs to be undone, run:
     ```powershell
     powershell -File scripts/vfs/revert_injected_override.ps1 -RelativePath "ui/scripts/store2.lua"
     ```

4. **Verify Archive Read:**
   - Confirm the client loads the asset cleanly from `.s2z` without relying on `override/`.

---

## 4. Mandatory Pre-Flight, Practice & Asset Safety Rules

### 1. Pre-Flight Syntax & Lint Checks (Crash Prevention)
Before triggering any hot-reload in a live game client, validate the modified file to prevent parsing crashes:
- **Lua Scripts:** Run `powershell -File scripts/verify_client_lua.ps1 -Path <path>` or check AST with `luals`.
- **XML Packages & Interfaces:** Run `powershell -File scripts/verify_xml_syntax.ps1 -Path <path>`.
- **MDF Model Audit:** Use TAO MCP `kongor_inspect_model_mdf` to verify bone names, mesh references, and materials.

### 2. Isolated Practice Mode & Visual Assertions
- Use isolated practice mode rather than public/lobby games for zero-friction testing:
  ```powershell
  powershell -File scripts/launch_screen_practice.ps1 -Hero Hero_Pyromancer -Width 1920 -Height 1080
  ```
  Or invoke TAO MCP `kongor_client_practice` with target hero/map.
- Assert rendered UI states and visual integrity:
  - `kongor_capture_window` — captures current client framebuffer.
  - `kongor_assert_visual` — automated pixel/layout comparison against golden baselines.
  - `kongor_ui_render_view` — inspects live widget hierarchy and visibility flags.

### 3. Orphan Process Sweeps Before Testing
Before starting test sessions, launching servers, or performing `.s2z` repacks, ensure no orphaned `hon_x64.exe` or `hon_server.exe` processes lock `.s2z` files or bind ports:
```powershell
Get-Process -Name hon_x64, hon_server -ErrorAction SilentlyContinue | Stop-Process -Force
```

### 4. Strict 32-bit Zip Offset Limits (< 1.5 GB)
- The K2 engine binary `k2_x64.dll` operates with 32-bit unsigned/signed zip offsets.
- Always enforce `allowZip64 = False` in all `.s2z` packing logic.
- Keep every individual `.s2z` archive strictly below **1.5 GB** (below the 2.0 GiB signed integer boundary). Never pack all game assets into a single monolithic archive; use the partitioned sets (`resources94.s2z` through `resources99.s2z`).

### 5. 35-Second MCP Command Liveness Check
- **Never Sit Idle:** When executing MCP commands during testing (`kongor_hot_reload`, `kongor_capture_window`, `kongor_game_command`, `kongor_game_lua`, Ghidra queries, or Frida sessions), check progress at least every **35 seconds**.
- If a command does not complete or log progress within 35 seconds, inspect task status, kill the stalled task (`manage_task Action: 'kill'`), document or bump the incident in `docs/reference/mcp-issues/` via `scripts/kongor_mcp_issues.ps1`, and verify engine/IPC readiness before re-attempting.

### 6. Mandatory VFS Precedence Verification
- **Mount Hierarchy:** K2 searches resources in strict left-to-right precedence order:
  `override` > `base` > `game` (`resources99.s2z` $\dots$ `resources0.s2z`) > `ClientPackageFiles` > `shared` > `KONGOR/*`
- **Verify Before Overriding:** Before modifying any asset or script, verify which file the engine is currently loading:
  - Run TAO MCP `kongor_vfs_resolve` or `kongor_vfs_locate` with the relative resource path (e.g. `ui/fe2/main_v2.interface`).
  - Or inspect archive contents via `scripts/vfs/read_archive_entry.ps1`.
- **Avoid Shadowing Surprises:** Confirm whether another archive or legacy folder already supplies the file so you do not modify a lower-precedence copy that gets silently ignored by the engine.

### 7. Perceptual Visual Baseline Diffing Protocol (Golden Screens)
For all UI modifications, store screen updates, HUD changes, and texture adjustments:
1. **Capture Baseline:** Before applying any code or layout changes, capture a baseline screenshot of the target screen:
   - Use TAO MCP `kongor_capture_window` or `kongor_client_capture_preview` and save the reference image.
2. **Apply in `override/` & Hot-Reload:** Validate syntax with pre-flight lint, place into `override/`, and reload via `kongor_hot_reload` or `kongor_client_eval_lua "Cmd('ReloadInterfaces')"`.
3. **Capture Post-Change Screen & Diff:** Capture the updated screen and run a visual comparison:
   - Use `kongor_ui_visual_diff` or `kongor_assert_visual` to compare against the golden baseline.
   - Verify that target widgets/buttons/text rendered correctly.
   - Check that adjacent UI elements, anchors, font paddings, and alignment remain identical to baseline (0 unintentional visual regression).
4. **Verification Gate:** Only when both visual diff confirms clean rendering AND logs confirm 0 Lua errors should the asset be injected into `.s2z`.

### 8. Mandatory Visual Proof Verification & Anti-False-Proof Invariant
- **The False-Proof Trap Strictly Prohibited:** Blindly executing a window capture (`kongor_capture_window`) while the client is sitting on the pre-login screen (`gamePhase 0: Login`), at a "Login Failed: Connection failed" modal, or on a blank loading screen and presenting it as "proof" of an in-game HUD feature, Store catalog item, or gameplay mechanic is strictly prohibited as false proof.
- **Pre-Capture State & Context Assertion:**
  Before capturing any proof screenshot, the agent MUST explicitly query and assert the active engine state via `kongor_game_client_manage action: 'status'` or `kongor_client_eval_lua`:
  1. *In-Game Features (HUD, Heartbeat Mod, Hero Spells, Range Rings):* The client must be in an active match or practice mode (`gamePhase == 5` / `isActiveMatch == true`), the relevant hero must be spawned in-world, and the specific target widget or visual state (e.g. low HP for heartbeat pulse) must be actively triggered and visible (`widget:IsVisible() == true`).
  2. *Front-End / Store / Vault / Codex:* The Aspire cluster (`master-server:55555`) must be running, the client must be authenticated past the login screen (`gamePhase == 1` / `GetWidget('main_top_bar'):IsVisible() == true`), and the target container or turntable (`store_container2`, `player_vault`, etc.) must be visibly displayed and focused.
  3. *Modals (Cinematic Theater, Options, Dialogs):* The modal widget (`cinematic_theater_modal`, `game_options_window`) must be visible (`modal:IsVisible() == true`) and rendering its content in front.
- **Mandatory Visual Content Inspection Gate:**
  Immediately after capturing any proof screenshot, the capturing agent MUST inspect the image:
  1. *Login Screen Rejection:* If the image displays the Empath login pedestal, "HEROES OF NEWERTH" header, or "Login Failed" dialog, the proof is automatically **DISQUALIFIED (REJECT)**. The agent must start required background services (e.g. `kongor_server_manage action: 'start' target: 'cluster'`) or launch into practice mode (`scripts/launch_screen_practice.ps1`) to reach the genuine screen.
  2. *Blank/Loading Screen Rejection:* If the screen is black, blank, or in transition, the proof is automatically **DISQUALIFIED (REJECT)**.
  3. *Target Semantic Check:* The agent must verify and describe the specific visual feature present in the image (e.g. "Abyssal Bubbles model loaded on turntable, cyan crystal staff glow billboard at bone RightHand visible, Store Alt Avatars grid showing Alt16 selected").
- **Automated Proof Inspection (`scripts/verify_proof_screenshot.py`):** Run `python scripts/verify_proof_screenshot.py --image <path> --expected <feature_type>` to programmatically verify that the screenshot is valid, non-blank, and not stuck on the login screen before citing it in commits, artifacts, or responses.

### 8. The Post-Reload / Post-Deploy Canary Observation Loop
> *"Don't declare victory the instant the command finishes; watch telemetry during the settle window."*

- **The Settle Window:** After triggering `kongor_hot_reload` or injecting into an `.s2z` archive, run a 30-to-60 second observation window:
  - Stream and inspect `games/client/console.log` and `games/client/override/logs/client_lua_debug.log`.
  - Verify that no delayed runtime errors fire when asynchronous UI timers expire or mouse-hover events trigger.
  - Check for asset streaming hitching or texture allocation warnings.
- **Pass Criteria:** The test run passes ONLY when the settle window completes with zero unhandled exceptions and stable engine telemetry.

### 9. Pre-Injection Secret Scrubbing Guard (CSO)
- **Automated Credential Guard:** `scripts/vfs/inject_override_to_s2z.ps1` includes an automated regex scanner that inspects file contents before injection.
- **Blocked Patterns:** Plaintext passwords (`password = "..."`), secret keys (`api_key = "..."`), private keys, and bearer tokens.
- **Failure Action:** If any hardcoded credential or token is detected, injection aborts immediately with `[SECURITY GATE FAILED]`. Do NOT pack un-scrubbed secrets into game archives.

### 10. Frame Budget & Hot-Loop Telemetry (from `/benchmark`)
- **Main Thread Execution Budget:** All K2 UI scripts, widget initializers, and periodic tick callbacks execute synchronously on the main engine thread.
- **The 144 FPS / 6.9ms Target:**
  - Individual Lua UI update handlers must complete in $\le 1.0\text{ ms}$.
  - Any tick callback or table traversal taking $> 5.0\text{ ms}$ constitutes frame hitching and must emit a warning to `client_lua_debug.log`.
- **Profile First:** Before modifying hot loops, profile with engine CVars (`Set host_timescale`, `profile`, or Stopwatch logging) to verify actual execution time rather than guessing.

### 11. Value-Weighted Testing Gate (The Test Value Bar)
> *"A test is valuable only if it enforces an engine contract, not because it touches lines of code."*

1. **The 3-Question Test Value Gate:**
   Every new or modified test suite (Lua tests, C# unit tests, harness scripts) must answer three questions:
   - **`protects`**: What public API contract, memory boundary, entity state, or network packet does this test guard?
   - **`fails_when`**: Under what realistic engine condition or code mutation will this test fail? (If a test cannot fail when the bug is reintroduced, it is a hollow test).
   - **`seam`**: Does the test verify real system boundaries (real `luac51` bytecode compilation, real `XmlDocument` loading, real IPC Named Pipe transmission), or does it rely on artificial mocks?
2. **Rejection of Hollow Tests:**
   - Pure existence checks (`assert(file != nil)`) and assertion-free smoke tests are rated weak ("★ tests") and do not satisfy the completion gate.
   - Tests coupled tightly to internal implementation details (breaking on refactors without behavior changes) must be retired.
3. **Preserve Full Test Concurrency (Concurrency Exposes Real Bugs):**
   - Keep full parallelization enabled across all unit and integration test suites. When a test fails, stalls, or misses a latency budget under concurrent execution, profile the concurrent run (`--logger "trx"`) and fix the underlying resource contention, uncached I/O, or race condition that concurrency exposed. Never disable parallelization (`DisableParallelization = true`, serial `[Collection]`, or `-maxcpucount:1`) to make a test pass.
4. **Slow-Test Manual Environment Variable Gate (`> 2.0s` $\to$ `KONGOR_RUN_MANUAL_TESTS` / `NEXUS_RUN_MANUAL_TESTS`):**
   - Any individual test taking longer than **2.0 seconds** (except one-time initial assembly/fixture initialization tests) must first be profiled for accidental uncached I/O or contention.
   - If the test is legitimately a long-running stress, soak, or multi-process test (`> 2.0s`), gate it via `if (!NexusTestEnvironment.IsManualTestEnabled) return;` (which checks `KONGOR_RUN_MANUAL_TESTS` and `NEXUS_RUN_MANUAL_TESTS` in `tests/shared/NexusTestEnvironment.cs`) and/or `[Trait("Category", "Manual")]` so default inner-loop and pre-push test runs remain fast.

### 12. Finite Probe Budgets & Hard Execution Deadlines
When executing exploratory QA, bot choreography validation, or crash investigations:
1. **Probe Ceiling:** Bounded exploration is capped at **10 discovery probes**. Never run indefinite exploratory loops.
2. **Hard Deadline:** Investigative loops must not exceed **5 minutes** of uninterrupted execution.
3. **Telemetry Receipt Flushing:** Intermediate observations are logged as `artifacts/exploration/exploration-NNN.json`. At the limit, halt foreground probing cleanly and report concrete findings to the user.

### 13. Native Hook Main-Thread & SEH Invariant (`version.dll` / `NativeGuard`)
1. **Window-Thread Enforcement (`KongorSubclassWndProc`):**
   - External graphics overlays (e.g. OBS `graphics-hook64.dll`) spawn background threads in `hon_x64.exe` that pump the Win32 message queue.
   - Any window procedure or IPC message handler in `src/tools/native-guard/` or `native/proxies/native_guard/` must verify:
     ```cpp
     DWORD dwWindowThread = GetWindowThreadProcessId(hWnd, NULL);
     if (GetCurrentThreadId() != dwWindowThread) {
         PostMessage(hWnd, WM_KONGOR_DISPATCH_CMD, wParam, lParam);
         return 0;
     }
     ```
2. **Asynchronous Command Queuing (`CConsole::AddCmdBuffer`):**
   - Never invoke `CConsole::Execute` (`g_pfnConsoleExecute`) synchronously from an IPC pipe or window subclass handler while the main thread may be executing `CHost::Execute` or `CLuaScriptManager` (`k2_x64.dll + 0x92BB66`).
   - Always queue commands via `CConsole::AddCmdBuffer` (`g_pfnConsoleAddCmdBuffer`) so K2 executes them deterministically on Thread 0 during `CConsole::Frame()`.
3. **Change-Signal Mailbox vs. Per-Frame `loadfile`:**
   - Never poll files per-frame in Lua using `loadfile`, which exhausts Lua's internal buffer allocator inside `luaV_concat` (`k2_x64.dll + 0xCB6477`). Use CVar change-signals (`tao_req`) so `loadfile` runs only when a request arrives.

### 14. Hermetic Scratch Isolation & Hard-Link Protection for AssetDB / Batch Tools
1. **Explicit Temp Root Requirement:**
   - Never run `tools/assetdb/sync_record.py`, `pytest tools/assetdb`, or `scripts/prebake-assets.ps1` against the live repository root by default; always pass an explicit `--repo-root <temp_dir>` or `-OutputDir <scratch_dir>`.
2. **Hard-Linked Archive Guard:**
   - Several `.s2z` archives (`resources0.s2z`, `resources5.s2z`, `resources98.s2z`) are hard-linked between `games/client/game/` and `games/server/game/`. Never edit them or `games/client/game/textures.s2z` / `resources94.s2z` in-place without checking `fsutil hardlink list` and receiving explicit user permission.

### 15. K2 UI & Rendering Footguns (`textures.s2z` Shadowing & `<modelpanel>` Stutter)
1. **Unshadowable UI Texture Basenames:**
   - In `CResourceManager::RegisterTexture`, legacy entries inside `games/client/game/textures.s2z` (`00000000/ui/...`) have special precedence that can silently shadow identically named loose textures in `games/client/override/`.
   - When replacing or resizing a UI texture whose path exists in `textures.s2z`, give the new asset a versioned, unshadowable basename (e.g. `tab38_*.dds`) and update the `.package`/`.interface` reference.
2. **`<modelpanel>` Animation Initialization & `scenebuffer`:**
   - Setting `anim="idle"` as a static XML attribute on a `<modelpanel>` causes `CModelPanel::Frame` (`k2_x64.dll + 0x951020`) to invoke `CSkeleton::StartAnim` on every frame tick, resetting `StartTime` to `Host.m_uiTime` ($\Delta t \equiv 0$) and freezing/stuttering the 3D skeleton.
   - Always omit the static XML `anim="..."` attribute, initialize animations once via `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`, and set `scenebuffer="false"` unless an offscreen render-target blit is strictly required.

### 16. Inner-Loop Gate & Cross-Tree Parity (`kongor-gate`)
Before declaring any code, UI, native hook, or protocol change complete:
1. **Cross-Tree Lua Parity (`Compare-Object`):** Verify that any modified shared script under `ui/scripts/newui/` is 100% byte-identical between `games/client/override/` and `games/client/ClientPackageFiles/`.
2. **Sub-5s 4-Tier Gate:** Execute `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1` and verify all 4 tiers (Roslyn `BannedSymbols`, 38 `LuaVerifier` contract suites, XML syntax lint, and `NativeGuard` smoke checks) pass `[PASS]`.
3. **Metagen Parity:** If `data/schemas/protocol.json` or shared memory contracts were touched, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/re/test_metagen_parity.ps1`.

### 17. Mandatory Visual Proof Verification & Semantic Truth Invariant ("Real Proof or No Proof")
1. **The False-Proof Trap Strictly Prohibited:**
   - Blindly executing a window capture (`kongor_capture_window`) while the client is sitting on the pre-login screen (`gamePhase 0: Login`), at a "Login Failed: Connection failed" modal, or on a blank loading screen and presenting it as "proof" of an in-game HUD feature, Store catalog item, or gameplay mechanic is strictly prohibited as false proof.
2. **Pre-Capture State & Context Assertion:**
   - Before capturing any proof screenshot, the agent MUST explicitly query and assert the active engine state via `kongor_game_client_manage action: 'status'` or `kongor_client_eval_lua`:
     - *In-Game Features (HUD, Heartbeat Mod, Hero Spells, Range Rings):* The client must be in an active match or practice mode (`gamePhase == 5` / `isActiveMatch == true`), the relevant hero must be spawned in-world, and the specific target widget or visual state (e.g. low HP for heartbeat pulse) must be actively triggered and visible (`widget:IsVisible() == true`).
     - *Front-End / Store / Vault / Codex:* The Aspire cluster (`master-server:55555`) must be running, the client must be authenticated past the login screen (`gamePhase == 1` / `GetWidget('main_top_bar'):IsVisible() == true`), and the target container or turntable (`store_container2`, `player_vault`, etc.) must be visibly displayed and focused.
     - *Modals (Cinematic Theater, Options, Dialogs):* The modal widget (`cinematic_theater_modal`, `game_options_window`) must be visible (`modal:IsVisible() == true`) and rendering its content in front.
3. **Mandatory Visual Content Inspection Gate:**
   - Immediately after capturing any proof screenshot, the capturing agent MUST inspect the image:
     - *Login Screen Rejection:* If the image displays the Empath login pedestal, "HEROES OF NEWERTH" header, or "Login Failed" dialog, the proof is automatically **DISQUALIFIED (REJECT)**. The agent must start required background services (e.g. `kongor_server_manage action: 'start' target: 'cluster'`) or launch into practice mode (`scripts/launch_screen_practice.ps1`) to reach the genuine screen.
     - *Blank/Loading Screen Rejection:* If the screen is black, blank, or in transition, the proof is automatically **DISQUALIFIED (REJECT)**.
     - *Target Semantic Check:* The agent must verify and describe the specific visual feature present in the image (e.g. "Abyssal Bubbles model loaded on turntable, cyan crystal staff glow billboard at bone RightHand visible, Store Alt Avatars grid showing Alt16 selected").
4. **Automated Proof Inspection (`scripts/verify_proof_screenshot.py`):**
   - Run `python scripts/verify_proof_screenshot.py --image <path> --expected <feature_type>` to programmatically verify that the screenshot is valid, non-blank, and not stuck on the login screen before citing it in commits, artifacts, or responses.

