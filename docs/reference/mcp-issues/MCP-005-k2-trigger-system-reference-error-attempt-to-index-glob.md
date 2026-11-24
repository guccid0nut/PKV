---
id: MCP-005
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "K2 Trigger System Reference Error (Attempt to Index Global 'LuaTrigger')"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "`ClientCommandRemediationService` auto-rewrites `LuaTrigger.GetTrigger(...)` $\to$ `GetTrigger(...)`."
---

# MCP-005: K2 Trigger System Reference Error (Attempt to Index Global 'LuaTrigger')

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Inspect active trigger parameters for reconnect address. |
| **Actual (Symptom)** | Failed with runtime error: `attempt to index global 'LuaTrigger' (a nil value)`. |
| **Root Cause** | `LuaTrigger` is not a global object in the K2 Lua environment. In K2, triggers are accessed via the global `GetTrigger(triggerName)` function or via event binding callbacks. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs`: Added auto-remediation rule transforming `LuaTrigger\.GetTrigger\s*\(([^)]+)\)` into `GetTrigger($1)`. The service automatically applies the rewrite and marks `selfHealed: true`. - Added unit test in `tests/unit/tools/Kongor.Mcp.Tests/ClientCommandRemediationTests... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  Script local lt = LuaTrigger.GetTrigger('ReconnectAddress')
  if not lt then return "LuaTrigger ReconnectAddress not found" end
  

Logs:

  [ERROR] [string "local lt = LuaTrigger.GetTrigger('Reconnect..."]:1: attempt to index global 'LuaTrigger' (a nil value)
  client_command_errors.ndjson: Id 22de9e0b, ErrorCategory: ExecutionError
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`ClientCommandRemediationService` auto-rewrites `LuaTrigger.GetTrigger(...)` $\to$ `GetTrigger(...)`.
