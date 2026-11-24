---
id: MCP-051
server: kongor
tool: kongor_game_lua
category: window-resolution
title: "kongor_game_lua Falling Back to In-Game Console When Game Window Not Found by Title"
status: resolved
verified_by: "McpToolContractTests.MCP002_And_MCP051_And_MCP119_LuaInterfaceRemediation_PidWindowTargeting_AndSyncEvalPayload"
superseded_by: ""
supersedes: ["MCP-045", "MCP-073", "MCP-108"]
occurrences: 4
date: 2026-09-25
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, window-resolution]
prevention_rule: "`UiControlService` resolves `K2_Class`/`K2_Vulkan` windows and explicit `pid:{id}` targeting."
---

# MCP-051: kongor_game_lua Falling Back to In-Game Console When Game Window Not Found by Title

## Summary
Tracked MCP issue on `kongor` / `kongor_game_lua` (category: `window-resolution`, status: `patched`, occurrences: 4). Lua script executes over IPC channel in game client. -> Failed with `Target game window 'hon_x64' not found. Ensure the game client is running.` in channel `in_game_console`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Lua script executes over IPC channel in game client. |
| **Actual (Symptom)** | Failed with `Target game window 'hon_x64' not found. Ensure the game client is running.` in channel `in_game_console`. |
| **Root Cause** | The client PID had changed/restarted to PID 32828. When IPC pipe `\\.\pipe\willowmaker-ipc` was momentary unavailable or reconnecting, the tool cascaded to `in_game_console` which searched for Win32 window title `hon_x64` before the window handle was indexed. |
| **Fix / Remediation** | Enhanced `UiControlService.ResolveWindowHandle` to support explicit `pid:{id}` parsing, search for K2 window classes (`K2_Class`, `K2_Vulkan`, `K2_Direct3D`), and filter out dedicated server processes using `ClientProcessSupervisor`. - Updated `GameLuaHandler.cs` and `GameClientManageHandler.cs` to target `$"pid:{actPid}"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "Player_Hosted_V2:Show(); Player_Hosted_V2:OpenCreate(); Player_Hosted_V2:OpenServerList()"
  }
  

Logs:

  Target game window 'hon_x64' not found. Ensure the game client is running.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`UiControlService` resolves `K2_Class`/`K2_Vulkan` windows and explicit `pid:{id}` targeting.
