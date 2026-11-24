---
id: MCP-198
server: kongor
tool: kongor_wait_for
category: runtime-error
title: "KONGOR-WAIT-FOR-IPC-READY-CONDITION-NAME"
status: investigating
codified_in: "src/tools/kongor-cli/Mcp/Handlers/WaitForHandler.cs"
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 119
date: 2026-10-13
last_seen: 2026-11-18
tags: [kongor, kongor_wait_for, runtime-error]
prevention_rule: "Use `kongor_game_client_manage` (`action: 'status'`) to check `ipcConnected: true`, or pass `condition: 'engine_ready'` to `kongor_wait_for`."
---

# MCP-198: KONGOR-WAIT-FOR-IPC-READY-CONDITION-NAME

## Summary
Tracked MCP issue on `kongor` / `kongor_wait_for` (category: `runtime-error`, status: `investigating`, occurrences: 119). Schema previously omitted `ipc_ready` and `client_ready`, and `condition: "lua"` failed when `target` included a leading `return ` statement (`return tostring((return ...))`). Patched in `McpToolRegistry.LuaUi.cs` and `WaitForHandler.cs`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | Poll `ipc_ready`, `engine_ready`, `client_ready`, or `lua` expressions (with or without a leading `return `) cleanly until satisfied. |
| **Actual (Symptom)** | Schema omitted `ipc_ready`/`client_ready` and `condition: "lua"` with `"return GetTime() > 0"` failed Lua compilation inside `tostring((...))`. |
| **Root Cause** | `McpToolRegistry.CreateWaitForDefinition` omitted `"ipc_ready"`, `"client_ready"`, and `"ui_hidden"` from `condition` `Enum`, and `WaitForHandler` wrapped raw `target` strings directly in `return tostring(({luaExpr}))` without stripping a leading `return` keyword or trailing semicolon. |
| **Fix / Remediation** | Added `"ipc_ready"`, `"client_ready"`, and `"ui_hidden"` to `McpToolRegistry.CreateWaitForDefinition` (`src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry.LuaUi.cs`) and added `WaitForHandler.NormalizeLuaWaitExpression` (`src/tools/kongor-cli/Mcp/Handlers/WaitForHandler.cs`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"condition": "ipc_ready", "timeoutMs": 15000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
Use `kongor_game_client_manage` (`action: 'status'`) to check `ipcConnected: true`, or pass `condition: 'engine_ready'` to `kongor_wait_for`.
