# 🌸 Together As One (TAO) — KONGOR MCP Server Installation & Integration Guide

> *"We Are As One"*  
> — **Empath**, *Heroes of Newerth*

This document provides complete instructions for developers and autonomous AI coding agents (Anthropic Claude, GitHub Copilot, Cursor, Windsurf, Google Antigravity) to install, configure, and communicate with the **Together As One (TAO)** MCP server for Project Kongor.

---

## 🧭 Identity & Disambiguation: Why TAO Never Confuses Agents

In development environments with multiple MCP servers (e.g. GitHub, Docker, Postgres, Memory), TAO is designed to be **unambiguous, self-identifying, and collision-free**:

| Property | Value | Notes |
|---|---|---|
| **Server Identifier (Config Key)** | `kongor` *(or alias `tao`)* | The standard key in `mcpServers` across all clients. |
| **Full Server Title** | **Together as One - KONGOR MCP (TAO)** | Declared in MCP `initialize` protocol handshake. |
| **Tool Namespace Prefix** | `kongor_*` | All 78+ tools start with `kongor_` (zero collisions with standard MCP tools). |
| **Protocol Version** | `2024-11-05` | Standard JSON-RPC 2.0 over `stdio`. |
| **Canonical Launcher Shim** | `src/tools/tao_mcp.py` / `src/tools/MCP.py` | Python shim with auto-compilation, shadow sandbox, and Win32 Job Object cleanup. |
| **Native .NET 11 Host** | `src/tools/kongor-cli/.../kongor.dll mcp` | Direct C# executable for zero-Python environments. |

> [!NOTE]
> Even though launcher files might be named `MCP.py` or `tao_mcp.py`, when an AI agent connects via MCP protocol, the server immediately returns:
> ```json
> {
>   "serverInfo": {
>     "name": "kongor",
>     "version": "5.0.0",
>     "description": "TAO (Together As One) - KONGOR Model Context Protocol Server for Project Kongor (named after Empath's ultimate ability)."
>   }
> }
> ```
> Every client agent instantly recognizes that this server is **TAO (Project Kongor)**.

---

## 🤖 Autonomous AI Agent Directives (For Claude, Copilot, Cursor Agents)

If a user or orchestrator instructs you: **"Install the TAO MCP server"** or **"Connect to the ProjectKongor MCP server"**, execute the following steps automatically:

1. **Detect Host Platform**: Check if you are running in Claude Desktop, Claude Code CLI, VS Code (GitHub Copilot), or Cursor.
2. **One-Command Automated Script**: If you have terminal access on Windows PowerShell, run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1 -Target All
   ```
   *(This automatically detects all installed tools, creates or merges JSON configs safely without overwriting other servers, and verifies the connection).*
3. **Manual JSON Config Fallback**: If you prefer direct file editing, locate the target configuration file from the matrix below, merge the `kongor` entry into `mcpServers`, and save.
4. **Smoke Test**: Execute a test tool call to verify connection:
   ```powershell
   python src/tools/tao_mcp.py --tool kongor_diagnostics
   ```

---

## 📁 Service Configuration Snippets

### 1. 🟣 Anthropic Claude Desktop

#### Configuration File Location:
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`  
  *(typically `%USERPROFILE%\AppData\Roaming\Claude\claude_desktop_config.json`)*
- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Linux**: `~/.config/Claude/claude_desktop_config.json`

#### Recommended Configuration (Python Shim):
Replace `<PROJECT_ROOT>` with your absolute path (e.g. `C:\\Path\\To\\ProjectKongor` or `/path/to/ProjectKongor`):
```json
{
  "mcpServers": {
    "kongor": {
      "command": "python",
      "args": [
        "<PROJECT_ROOT>\\src\\tools\\tao_mcp.py"
      ],
      "env": {
        "PYTHONUNBUFFERED": "1"
      }
    }
  }
}
```

#### Native .NET 11 Host Configuration (Alternative):
```json
{
  "mcpServers": {
    "kongor": {
      "command": "dotnet",
      "args": [
        "<PROJECT_ROOT>\\src\\tools\\kongor-cli\\bin\\Release\\net11.0\\kongor.dll",
        "mcp"
      ],
      "env": {
        "KONGOR_ENABLE_SELF_HEALING": "true"
      }
    }
  }
}
```

---

### 2. ⚡ Claude Code (Anthropic CLI)

You can add TAO to Claude Code with a single CLI command:

#### Global Registration:
```bash
claude mcp add kongor python "<PROJECT_ROOT>/src/tools/tao_mcp.py"
```

#### Project-Scoped Registration (Inside ProjectKongor repository):
```bash
claude mcp add --scope project kongor python "src/tools/tao_mcp.py"
```

To verify active servers in Claude Code:
```bash
claude mcp list
```

---

### 3. 🐱 GitHub Copilot & VS Code

VS Code and GitHub Copilot support MCP servers through `.vscode/mcp.json`.

#### Workspace Configuration (`.vscode/mcp.json`):
This file is pre-configured in the repository root at `.vscode/mcp.json`:
```json
{
  "mcpServers": {
    "kongor": {
      "command": "python",
      "args": [
        "${workspaceFolder}/src/tools/tao_mcp.py"
      ],
      "env": {
        "PYTHONUNBUFFERED": "1"
      }
    }
  }
}
```

#### How GitHub Copilot uses TAO:
- GitHub Copilot automatically indexes `.github/copilot-instructions.md` in the repository, learning about the `kongor_*` tool catalog, engine guardrails, and Lua scripting conventions.
- When chatting with Copilot Chat (`@workspace`), ask: *"Check dedicated server status with kongor"* or *"Run kongor_diagnostics"*.

---

### 4. 🖱️ Cursor IDE

Cursor natively discovers MCP servers from workspace or global settings.

#### Workspace Configuration (`.cursor/mcp.json`):
This file is pre-configured in the repository root at `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "kongor": {
      "command": "python",
      "args": [
        "${workspaceFolder}/src/tools/tao_mcp.py"
      ],
      "env": {
        "PYTHONUNBUFFERED": "1"
      }
    }
  }
}
```

#### Manual GUI Registration in Cursor:
1. Open **Cursor Settings** (`Ctrl+,` or `Cmd+,`).
2. Navigate to **Features** → **MCP**.
3. Click **Add New MCP Server**.
4. Set:
   - **Name**: `kongor`
   - **Type**: `command`
   - **Command**: `python <PROJECT_ROOT>\src\tools\tao_mcp.py`

---

### 5. 🌊 Windsurf (Codeium)

#### Configuration File Location:
- `~/.codeium/windsurf/mcp_config.json`

#### Configuration:
```json
{
  "mcpServers": {
    "kongor": {
      "command": "python",
      "args": [
        "<PROJECT_ROOT>/src/tools/tao_mcp.py"
      ],
      "env": {
        "PYTHONUNBUFFERED": "1"
      }
    }
  }
}
```

---

### 6. 🌐 Google Antigravity & Gemini

Register schemas and guidelines with the native CLI command:
```powershell
# Auto-generate schema JSON definitions into ~/.gemini/antigravity/mcp/kongor/
kongor mcp --register-antigravity
```

---

## ⚡ One-Click Automated Setup Script (`scripts/dev/install_tao_mcp.ps1`)

Project Kongor includes a dedicated PowerShell installer that configures all your local AI assistants in seconds:

```powershell
# Install into all detected clients (Claude Desktop, Copilot/VS Code, Cursor):
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1

# Target a specific client only:
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1 -Target Claude
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1 -Target Copilot
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1 -Target Cursor

# Preview changes without modifying files:
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1 -DryRun
```

### What the installer does:
- ✅ **Resolves Absolute Paths**: Dynamically inspects the current repository location.
- ✅ **Safe Merging**: Parses existing `claude_desktop_config.json` / `mcp.json` files and inserts or updates `kongor` while preserving all other existing servers.
- ✅ **Pre-flight Smoke Test**: Executes `python src/tools/tao_mcp.py --tool kongor_diagnostics` to ensure the server starts properly.
- ✅ **Extensive Logging**: Emits clear step-by-step diagnostic output in Windows PowerShell.

---

## 🧪 Testing & Verifying Installation

### 1. Direct CLI Tool Execution (No MCP Client Needed)
You can test any TAO tool immediately from your terminal:
```powershell
# Health and endpoint diagnostics
python src/tools/tao_mcp.py --tool kongor_diagnostics

# Inspect running game client status
python src/tools/tao_mcp.py --tool kongor_game_client_manage --args '{"action":"status"}'

# Dedicated server supervisor status
python src/tools/tao_mcp.py --tool kongor_compel_manage --args '{"action":"status"}'
```

### 2. Standard MCP Stdio Handshake Verification
Run the verification script:
```powershell
python scripts/qa/invoke_tao_mcp.py kongor_health_check
```
This performs a full JSON-RPC 2.0 handshake (`initialize` -> `notifications/initialized` -> `tools/call`) and outputs the response.

---

## 🛡️ Critical Engine Guardrails for AI Agents

When constructing tool calls or Lua scripts for the K2 engine, AI agents must adhere to these rules:

1. **Global UI Manager is `UIManager`, NEVER `Windows`**:
   - ❌ *Incorrect*: `Windows.main:GetWidget('button')` (causes `attempt to index global 'Windows' (nil value)`).
   - ✅ *Correct*: `UIManager.GetInterface('main'):GetWidget('widget_name')`.
2. **`DoEvent()` Signature Rules**:
   - `widget:DoEvent()` takes **no parameters** (triggers XML `onevent`).
   - `widget:DoEventN(index)` takes an integer index (`0`, `1`, `2`) to trigger `onevent0`, `onevent1`.
   - ❌ *Never pass strings*: `widget:DoEvent('onclick')` is invalid in K2.
3. **Dedicated Server Command Routing**:
   - When dispatching console commands to dedicated servers (`hon_x64.exe -dedicated`), **always provide the target listen port** (e.g. `port: 11235`):
     ```json
     { "name": "kongor_game_server_command", "arguments": { "port": 11235, "command": "AddFakePlayer 2" } }
     ```
4. **Bypass UI Clicks for Practice Matches**:
   - Do not simulate multi-step UI clicks to start games.
   - Use `kongor_match_launch`:
     ```json
     { "name": "kongor_match_launch", "arguments": { "mode": "practice", "map": "caldavar", "hero": "Hero_Pyromancer" } }
     ```
5. **Anti-Chat Spill Invariant**:
   - Never send keyboard simulation when in-game chat prompt (`>`) is focused.
   - Use `kongor_ui_control action: "dismiss_console"` before typing commands.
6. **K2 Lua Method Existence Checks Must Use Dot Access (`.` NOT `:`)**:
   - ❌ *Incorrect*: `if widget:GetWidget and widget:GetWidget('btn') then` (causes `syntax error: unexpected symbol near 'and'`).
   - ✅ *Correct*: `if widget.GetWidget and widget:GetWidget('btn') then`. In Lua, colon syntax requires argument parentheses. TAO auto-remediates this via `[MCP-078]`, but direct adherence is best practice.
7. **K2 Lua Widget Root Access**:
   - ❌ *Incorrect*: `CInterface:GetRootWidget()` (causes `attempt to call method 'GetRootWidget' (a nil value)`).
   - ✅ *Correct*: `UIManager.GetInterface('main'):GetWidget('widget_name')` or `interface:GetWidget('main')` (`[MCP-079]`).
8. **UI Automation Window Targeting & Stale PID Resilience**:
   - `kongor_ui_control` defaults target to `"hon_x64"`.
   - If passing an explicit `target: "pid:<id>"`, if the PID terminates or has no window handle, TAO's UI Control layer automatically discovers the active client PID and migrates window handle binding (`[MCP-069]`, `[MCP-074]`).
9. **Redis MCP Tool Calling Flexibility**:
   - For `hset`, `set`, `hgetall`, `hget`: The resilient wrapper transparently accepts `name` or `key`, and `key` or `field`.
   - For `hset` and `set`: You can pass structured JSON dictionaries or lists directly in `value` (e.g. `{"status": "ready"}`); the wrapper automatically serializes them into valid JSON strings to prevent Pydantic schema rejection (`[MCP-026]`).
10. **Ghidra MCP Binary Selection & Hex Addresses**:
    - `binary_name` is optional across `search_strings`, `search_code`, `search_symbols_by_name`, `decompile_function` (defaults to `"k2_x64.dll"`, `[MCP-070]`).
    - Addresses may be passed in hex format (e.g. `"0x1808ea4f0"` or `"1808ea4f0"`), and are resolved directly via Ghidra's `AddressFactory` (`[MCP-007]`).
11. **Mandatory MCP Command Failure Logging (`docs/MCP_ISSUES.md`)**:
    - Whenever ANY MCP command fails, times out, or behaves unexpectedly, immediately log it to [`docs/MCP_ISSUES.md`](../MCP_ISSUES.md) with exact parameters, problem analysis, logs, and a concrete next-time prevention rule so future calls succeed on the first attempt.
12. **Clean-Room Reverse Engineering & Evidence Dossiers (`export_evidence_dossier`)**:
    - Whenever reverse-engineering proprietary mechanics or packet flows from HoN engine binaries, call `export_evidence_dossier` via the `ghidra` MCP server.
    - Specify `binary_name` (e.g. `"game_x64.dll"`), `name_or_address` (e.g. `"0x180035d40"`), and `output_category` (e.g. `"packets"`, `"abilities"`, `"physics"`).
    - The tool automatically generates IDA-style SigScan byte masks, extracts calling conventions, parameters, VTable indices, decompiled pseudo-C, and writes to `artifacts/evidence/{category}/{dossier_id}.json`.
    - It returns the file paths AND the full dossier JSON in one call (Dual Payload Return).
    - Use `status: "draft"` during active exploration. Promote to `"verified"` once clean-room C# code and unit tests are linked.
    - Run `python tests/unit/tools/test_evidence_dossiers.py` (< 0.05s) to validate all dossiers offline.

---

## 🧰 Top Tools Catalog Quick Reference

| Tool | Primary Purpose | Example Call |
|---|---|---|
| `kongor_match_launch` | Launch practice, bot, or public matches directly | `{"action": "launch", "mode": "practice", "hero": "Hero_Pyromancer"}` |
| `kongor_game_command` | Dispatch K2 engine commands or CVars via IPC | `{"command": "Set vid_maxFPS 1000"}` |
| `kongor_game_lua` | Execute or evaluate Lua expressions in client | `{"code": "Cmd('StartGame practice ...')"}` |
| `kongor_capture_window` | Tier 0 Vulkan swapchain offscreen window capture | `{"target": "hon_x64", "capture_method": "auto", "save_artifact": true}` |
| `kongor_game_server_command` | Route console commands to dedicated server port | `{"port": 11235, "command": "AddFakePlayer 1"}` |
| `kongor_game_client_manage` | Start, stop, or inspect `hon_x64.exe` client | `{"action": "status"}` |
| `kongor_server_manage` | Control Aspire microservices (Master, Chat, Web) | `{"action": "status", "target": "cluster"}` |
| `kongor_compel_manage` | Scale and inspect dedicated server supervisor | `{"action": "status"}` |
| `kongor_diagnostics` | Full health audit across all ports and microservices | `{"scope": "all"}` |
| `kongor_self_heal` | Ingest engine logs and trigger auto-heal reflexes | `{"action": "scan_and_ingest", "log_path": "console.log"}` |

---

## 🌸 Summary

With **TAO**, your AI assistant hops inside ProjectKongor just like Empath channels *Together As One*. Whether running in Claude Desktop, Claude Code, GitHub Copilot, Cursor, or Antigravity, TAO provides safe, high-speed, self-healing programmatic control over every engine subsystem.
