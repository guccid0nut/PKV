---
id: MCP-225
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-TIMEOUT-WITHOUT-LIVENESS-CHECK"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-16
last_seen: 2026-10-16
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Before invoking live client IPC commands, check `Get-Process hon_x64 -ErrorAction SilentlyContinue` or `kongor_game_client_manage` (`action: 'status'`), and use `kongor_game_client_manage` (`action: 'command'`) when `WILLOWMAKER.exe` is act..."
---

# MCP-225: KONGOR-CLIENT-EVAL-LUA-TIMEOUT-WITHOUT-LIVENESS-CHECK

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate Lua inspection snippet for `store2_vanity_frame_icons_1_1_frame_over` in `hon_x64.exe`. |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 499) timed out after 3000ms.` |
| **Root Cause** | `kongor_client_eval_lua` was invoked without first verifying whether `hon_x64.exe` was running or whether `WILLOWMAKER.exe` was intercepting `\\.\pipe\willowmaker-ipc` (as documented in `[MCP-221]`). |
| **Fix / Remediation** | Before invoking live client IPC commands, check `Get-Process hon_x64 -ErrorAction SilentlyContinue` or `kongor_game_client_manage` (`action: "status"`), and use `kongor_game_client_manage` (`action: "command"`) when `WILLOWMAKER.exe` is active. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"code": "local w = GetWidget('store2_vanity_frame_icons_1_1_frame_over'); ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Before invoking live client IPC commands, check `Get-Process hon_x64 -ErrorAction SilentlyContinue` or `kongor_game_client_manage` (`action: 'status'`), and use `kongor_game_client_manage` (`action: 'command'`) when `WILLOWMAKER.exe` is act...
