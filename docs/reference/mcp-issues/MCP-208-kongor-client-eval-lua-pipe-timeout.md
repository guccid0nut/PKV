---
id: MCP-208
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-PIPE-TIMEOUT"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Use `kongor_game_client_manage` (`action: 'command'`, `command: 'UICall * \'Trigger(...)\''`) which routes directly through `kongor-native-client` (`version.dll`)."
---

# MCP-208: KONGOR-CLIENT-EVAL-LUA-PIPE-TIMEOUT

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Execute Lua snippet over IPC and return result. |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 376) timed out after 3000ms.` |
| **Root Cause** | `kongor_client_eval_lua` targets `willowmaker-ipc` rather than `kongor-native-client` (`native_guard`), whereas `kongor_game_client_manage` (`action: "command"`) dispatches over `kongor-native-client` in ~7ms. |
| **Fix / Remediation** | Use `kongor_game_client_manage` (`action: "command"`, `command: "UICall * \"Trigger(...)\""`) which routes directly through `kongor-native-client` (`version.dll`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Use `kongor_game_client_manage` (`action: 'command'`, `command: 'UICall * \'Trigger(...)\''`) which routes directly through `kongor-native-client` (`version.dll`).
