---
id: MCP-085
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "`kongor_game_lua` Attempt to Index Lua Module on `CInterface` Object Instead of Global Scope `_G`"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "In K2 Lua scripts, access Lua tables and helper singletons like `Player_Hosted_V2` directly via `Player_Hosted_V2` (or `_G.Player_Hosted_V2`), using `UIManager.GetInterface('main')` only for widget queries (`:GetWidget('widget_name')`)."
---

# MCP-085: `kongor_game_lua` Attempt to Index Lua Module on `CInterface` Object Instead of Global Scope `_G`

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Calling `GetLowestPingServer` through `Player_Hosted_V2`. |
| **Actual (Symptom)** | Failed with `[ERROR] [string "..."]:3: attempt to index field 'Player_Hosted_V2' (a nil value)`. |
| **Root Cause** | `Player_Hosted_V2` is a table defined in the global Lua environment `_G`, not a member variable of the C++ `CInterface` userdata returned by `UIManager.GetInterface('main')`. |
| **Fix / Remediation** | Direct call via `Player_Hosted_V2:GetLowestPingServer(...)`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local main = UIManager.GetInterface('main')\nlocal reg = main._CreateSetting and main._CreateSetting.region or 'ALL'\nlocal srv, ping = main.Player_Hosted_V2:GetLowestPingServer(reg)\nreturn 'region=' .. tostring(reg) .. ', srv=' .. tostring(srv) .. ', ping=' .. tostring(ping)"
  }
  

Logs:

  [ERROR] [string "local main = UIManager.GetInterface('main')..."]:3: attempt to index field 'Player_Hosted_V2' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
In K2 Lua scripts, access Lua tables and helper singletons like `Player_Hosted_V2` directly via `Player_Hosted_V2` (or `_G.Player_Hosted_V2`), using `UIManager.GetInterface('main')` only for widget queries (`:GetWidget('widget_name')`).
