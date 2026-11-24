---
id: MCP-093
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "`kongor` / `kongor_game_lua` Execution Error on getTriggerParams (LT nil)"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "Avoid calling `getTriggerParams` directly in root scripts; query trigger fields directly on trigger userdata instances or check registered watches."
---

# MCP-093: `kongor` / `kongor_game_lua` Execution Error on getTriggerParams (LT nil)

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Retrieve trigger parameter names for 'ConnectionStatus'. |
| **Actual (Symptom)** | Failed with `[ERROR] /ui/scripts/global_main.lua:2239: attempt to index global 'LT' (a nil value)`. |
| **Root Cause** | `getTriggerParams` in `global_main.lua` expects the global `LT` (`LuaTrigger`) table to be initialized, which is not populated in root execution context without explicit initialization. |
| **Fix / Remediation** | Avoid calling `getTriggerParams` directly in root scripts; query trigger fields directly on trigger userdata instances or check registered watches. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local p = getTriggerParams('ConnectionStatus') ... return 'Params: ' .. table.concat(res, ', ')"
  }
  

Logs:

  [ERROR] /ui/scripts/global_main.lua:2239: attempt to index global 'LT' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Avoid calling `getTriggerParams` directly in root scripts; query trigger fields directly on trigger userdata instances or check registered watches.
