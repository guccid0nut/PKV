# 🤖 GitHub Copilot Instructions — Project Kongor & TAO MCP Server

> **Authoritative Reference:** Always adhere to root **[`AGENTS.md`](../AGENTS.md)** ("Kongor-Tail") for engineering philosophy, crash triage, and verification gates.

You are operating inside **Project Kongor**, the modernized Heroes of Newerth (v4.10.1 / K2 Engine) ecosystem powered by **.NET 11.0 (`net11.0`)**.

---

## 🌸 1. Together As One (TAO) — KONGOR MCP Server (`kongor`)

The repository includes an embedded, self-healing Model Context Protocol (MCP) server named **TAO (Together as One)**, named after Empath's ultimate ability:
- **Server Key**: `kongor` (configured in `.vscode/mcp.json` and `.cursor/mcp.json`).
- **Tool Prefix**: `kongor_*` (**specialized tools** across functional handler domains).
- **Native .NET 11 Host**: `kongor mcp` (`src/tools/kongor-cli/Mcp/McpServerCommand.cs`) and Python shadow-runtime shim `src/tools/tao_mcp.py`.
- **Zero-Focus-Theft In-Engine IPC (`CompositeGameIpcBridge`)**:
  - Primary: **`NexusMailboxIpcBridge`** (`tao_req` / `tao_ack` CVar + file mailbox polled in Lua) and **`NativeClientIpcBridge`** (`\\.\pipe\KongorNativeGuard_*` inside `version.dll`, queued to the main-thread ring via `CConsole::AddCmdBuffer`).
  - Never steals OS foreground window focus or disrupts the developer's mouse/keyboard.
- **Full Documentation**: [`docs/tao-mcp/overview.md`](../docs/tao-mcp/overview.md) and [`src/tools/kongor/instructions.md`](../src/tools/kongor/instructions.md).

---

## 🎯 2. Surgical Client VFS Override Workflow (`games/client/override/`)

1. **Keep `games/client/override/` Clean (0 Clutter Files at Baseline):**
   - Never unpack entire `.s2z` archives into `override/`.
2. **Locate & Resolve Before Editing (`VfsIndexService`):**
   - Call `kongor_vfs_locate` (substring search across all `.s2z` Central Directories in $<1\text{ ms}$) and `kongor_vfs_resolve` (verifies precedence across `override` > `base` > `game/resources999..0.s2z` > `ClientPackageFiles` > `textures.s2z`).
3. **Surgical Single-File Extraction:**
   - Extract only the target file with `scripts/vfs/read_archive_entry.ps1 -ArchivePath <s2z> -EntryPath <path> -OutputPath games/client/override/<path>`.
   - Mirror shared UI modifications to `games/client/ClientPackageFiles/<path>` to maintain 100% cross-tree parity.
4. **Pre-Flight Lint, Hot-Reload & Gate:**
   - Run `scripts/verify_client_lua.ps1` and `scripts/verify_xml_syntax.ps1`.
   - Hot-reload live via `kongor_hot_reload` or `kongor_client_eval_lua`, verify visually (`kongor_capture_window` / `kongor_ui_visual_diff`), and run `scripts/gate_check.ps1`.
   - Commit directly in `override/` or bake into `.s2z` on-demand via `scripts/vfs/inject_override_to_s2z.ps1` (5-tier `.bak` rotation).
   - Full guide: [`docs/how-to/client-override-vfs-workflow.md`](../docs/how-to/client-override-vfs-workflow.md).

---

## 🛡️ 3. Mandatory Engine & Tool Guardrails

1. **Windows PowerShell Compliance**:
   - Always use native PowerShell cmdlets (`Get-ChildItem`, `Select-String`, `Copy-Item`, `Remove-Item`). Never use Linux commands like `grep` or `cp`.
2. **Dedicated Game Server Command Routing**:
   - When sending console commands to dedicated servers (`hon_x64.exe -dedicated`), **always specify the target listen port** (e.g. `port: 11235`) via `kongor_game_server_command`.
   - Populate bot/practice slots on dedicated servers with `AddFakePlayer 1` or `AddFakePlayer 2`.
3. **K2 UI & Lua Scripting Conventions**:
   - **Global UI Manager is `UIManager`** (NEVER `Windows`). Example: `UIManager.GetInterface('main'):GetWidget('widget_name')`.
   - **`DoEvent()` Signature**: `widget:DoEvent()` takes **no arguments**. `widget:DoEventN(0)` takes an integer index. Never pass `'onclick'` to `DoEvent`.
   - **`<modelpanel>` Animation Rule**: Never set `anim="idle"` as a static XML attribute on a `<modelpanel>` (causes `CModelPanel::Frame` to reset `StartTime` every tick). Use `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`.
   - **`textures.s2z` Shadowing Rule**: Legacy paths inside `games/client/game/textures.s2z` (`00000000/ui/...`) cannot be shadowed by identically named files in `override/`. Use a versioned basename (e.g. `tab38_*.dds`) and update the XML reference.
4. **Instant Log & Crash Dump Triangulation (`Everything` $\to$ `WinDbg` $\to$ `Ghidra` $\to$ `Frida`)**:
   - Always use `everything_search` (`ext:log|ext:dmp`, `sort: "date-modified-desc"`) to find logs and minidumps in milliseconds—never run slow recursive `Get-ChildItem -Recurse` scans.
   - Inspect dumps with `windbg` (`open_cdb_dump`, `!analyze -v`), decompile RVAs with `ghidra` (`ProjectKongor.rep`), and verify dynamically with `frida`.
5. **Mandatory MCP Issue Logging (`docs/MCP_ISSUES.md`)**:
   - Whenever ANY MCP command fails, times out, or behaves unexpectedly, immediately record a structured entry in [`docs/MCP_ISSUES.md`](../docs/MCP_ISSUES.md).

---

## 🧰 4. Quick Tool Selection Matrix

| Task | Recommended Tool |
|---|---|
| Locate any file across `.s2z` archives in $<1\text{ ms}$ | `kongor_vfs_locate` / `kongor_vfs_resolve` |
| Hot-reload Lua/XML in running client (zero focus theft) | `kongor_hot_reload` / `kongor_client_eval_lua` |
| Capture background client screenshot & perceptual diff | `kongor_capture_window` / `kongor_ui_visual_diff` |
| Check client console & Lua errors since timestamp | `kongor_errors_since` / `kongor_get_client_logs` |
| Launch a practice or bot match | `kongor_match_launch` / `kongor_client_practice` |
| Send command to dedicated server port | `kongor_game_server_command` |
| Inspect or manage COMPEL dedicated fleet | `kongor_compel_manage` / `kongor_game_server_manage` |
| Inspect or manage Aspire microservices | `kongor_server_manage` |
| Check overall ecosystem health | `kongor_diagnostics` / `kongor_health_check` |
