---
id: MCP-221
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-TIMEOUT-WHEN-WILLOWMAKER-ACTIVE"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Always use `kongor_game_client_manage` (`action: 'command'`) to execute `Script ...` on `hon_x64.exe` when `WILLOWMAKER.exe` is running."
---

# MCP-221: KONGOR-CLIENT-EVAL-LUA-TIMEOUT-WHEN-WILLOWMAKER-ACTIVE

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate widget state of `midbar_button_hontour` and `sysbar_news_button` in `hon_x64.exe` (`PID 100828`). |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 475) timed out after 3000ms.` |
| **Root Cause** | `kongor_client_eval_lua` targets `\\.\pipe\willowmaker-ipc` when `WILLOWMAKER.exe` is open, whereas `hon_x64.exe` listens on `\\.\pipe\kongor-native-client`. |
| **Fix / Remediation** | Always use `kongor_game_client_manage` (`action: "command"`) to execute `Script ...` on `hon_x64.exe` when `WILLOWMAKER.exe` is running. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "local m = UIManager.GetInterface('main'); local hontour = m:GetWidget('midbar_button_hontour') ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Always use `kongor_game_client_manage` (`action: 'command'`) to execute `Script ...` on `hon_x64.exe` when `WILLOWMAKER.exe` is running.
