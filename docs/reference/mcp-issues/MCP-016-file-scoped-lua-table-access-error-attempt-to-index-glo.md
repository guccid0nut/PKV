---
id: MCP-016
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "File-Scoped Lua Table Access Error (Attempt to Index Global '_serverList')"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "Access module-level functions (e.g. `Player_Hosted_V2:OnClick*`) rather than file-scoped locals."
---

# MCP-016: File-Scoped Lua Table Access Error (Attempt to Index Global '_serverList')

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Access file-local table `_serverList` to query server metadata. |
| **Actual (Symptom)** | Threw Lua runtime error: `attempt to index global '_serverList' (a nil value)`. |
| **Root Cause** | In `player_hosted_v2.lua`, `_serverList` and `_selectedServer` are declared as file-local (`local _serverList = {}`), not in the global table `_G`. Executing arbitrary expressions in global script context cannot access local variables declared in other file scopes. |
| **Fix / Remediation** | Drive UI actions through `Player_Hosted_V2:OnClick*` methods instead of direct local table access. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "local s = _serverList[_selectedServer] if s then Echo('L1_INFO: ip=' .. tostring(_selectedServer) .. ' name=' .. tostring(s.name) .. ' port=' .. tostring(s.port)) else Echo('L1_INFO: not found for ' .. tostring(_selectedServer)) end",
    "target": "client"
  }
  

Logs:

  [ERROR] [string "local s = _serverList[_selectedServer] if s..."]:1: attempt to index global '_serverList' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Access module-level functions (e.g. `Player_Hosted_V2:OnClick*`) rather than file-scoped locals.
