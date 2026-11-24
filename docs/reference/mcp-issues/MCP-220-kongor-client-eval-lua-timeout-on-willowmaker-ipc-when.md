---
id: MCP-220
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-TIMEOUT-ON-WILLOWMAKER-IPC-WHEN-LAUNCHER-OPEN"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "When `WILLOWMAKER.exe` is open alongside `hon_x64.exe`, always use `kongor_game_client_manage` (`action: 'command'`) or direct `\\.\pipe\kongor-native-client` pipe calls instead of `kongor_client_eval_lua`."
---

# MCP-220: KONGOR-CLIENT-EVAL-LUA-TIMEOUT-ON-WILLOWMAKER-IPC-WHEN-LAUNCHER-OPEN

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate Lua expression on the running `hon_x64.exe` (`PID 48624`) and return the state string. |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 472) timed out after 3000ms.` |
| **Root Cause** | As documented in `MCP-208` and `MCP-213`, the long-lived IDE stdio MCP server process (`kongor`) routes `kongor_client_eval_lua` over `willowmaker-ipc` when `WILLOWMAKER.exe` launcher is open, whereas `hon_x64.exe`'s native guard listens on `\\.\pipe\kongor-native-client` (`kongor_game_client_manage action: "command"`). |
| **Fix / Remediation** | When `WILLOWMAKER.exe` is open alongside `hon_x64.exe`, always use `kongor_game_client_manage` (`action: "command"`) or direct `\\.\pipe\kongor-native-client` pipe calls instead of `kongor_client_eval_lua`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return \"isLoggedIn=\" .. tostring(_G.isLoggedIn) ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
When `WILLOWMAKER.exe` is open alongside `hon_x64.exe`, always use `kongor_game_client_manage` (`action: 'command'`) or direct `\\.\pipe\kongor-native-client` pipe calls instead of `kongor_client_eval_lua`.
