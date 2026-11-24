---
id: MCP-049
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "Nil Global Reference _CreateSetting in Lua Diagnostic Script"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "Access module functions or UI widget values rather than file-scoped locals."
---

# MCP-049: Nil Global Reference _CreateSetting in Lua Diagnostic Script

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Echo the selected server and create setting status. |
| **Actual (Symptom)** | Failed with `attempt to index global '_CreateSetting' (a nil value)`. |
| **Root Cause** | In `player_hosted_v2.lua`, `_CreateSetting` is declared as a local table at module scope, not as a global. Injected Lua scripts from the outside cannot access module-local upvalues directly via the global environment. |
| **Fix / Remediation** | Query state via UI widgets (`widget:GetValue()`) rather than module-local variables. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "if Player_Hosted_V2 then Player_Hosted_V2:OnClickServer(1); Player_Hosted_V2:OnClickSelectServer(); Echo(... _CreateSetting['server'] ...) end"
  }
  

Logs:

  [ERROR] [string "if Player_Hosted_V2 then..."]:5: attempt to index global '_CreateSetting' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Access module functions or UI widget values rather than file-scoped locals.
