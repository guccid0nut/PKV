---
id: MCP-215
server: kongor
tool: kongor_client_eval_lua
category: client-offline
title: "KONGOR-CLIENT-EVAL-LUA-IPC-TIMEOUT-WHEN-CLIENT-OFFLINE"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: []
occurrences: 31
date: 2026-10-15
last_seen: 2026-11-17
tags: [kongor, kongor_client_eval_lua, client-offline]
prevention_rule: "Always invoke `kongor_game_client_manage` (`action: 'status'`) first to verify `running: true` and `ipcConnected: true` before calling `kongor_client_eval_lua`, and launch `hon_x64.exe` via `kongor_game_client_manage` (`action: 'launch'`) w..."
---

# MCP-215: KONGOR-CLIENT-EVAL-LUA-IPC-TIMEOUT-WHEN-CLIENT-OFFLINE

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `client-offline`, status: `codified`, occurrences: 31). Evaluate Lua snippet on live client to check login and interface status. -> Returned `IPC request 'ExecuteCommand' (RequestId: 435) timed out after 3000ms.` because `hon_x64.exe` was not running.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate Lua snippet on live client to check login and interface status. |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 435) timed out after 3000ms.` because `hon_x64.exe` was not running. |
| **Root Cause** | `kongor_client_eval_lua` was invoked before checking `kongor_game_client_manage` (`action: "status"`), while `hon_x64.exe` was offline (`running: false`). |
| **Fix / Remediation** | Always invoke `kongor_game_client_manage` (`action: "status"`) first to verify `running: true` and `ipcConnected: true` before calling `kongor_client_eval_lua`, and launch `hon_x64.exe` via `kongor_game_client_manage` (`action: "launch"`) when offline. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return tostring(HoN_Main and HoN_Main.isFullyLoggedIn), tostring(UIManager.GetInterface('main'))"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Always invoke `kongor_game_client_manage` (`action: 'status'`) first to verify `running: true` and `ipcConnected: true` before calling `kongor_client_eval_lua`, and launch `hon_x64.exe` via `kongor_game_client_manage` (`action: 'launch'`) w...
