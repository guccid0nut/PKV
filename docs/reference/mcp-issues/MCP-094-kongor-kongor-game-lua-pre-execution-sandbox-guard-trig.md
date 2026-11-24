---
id: MCP-094
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "`kongor` / `kongor_game_lua` Pre-Execution Sandbox Guard Triggered (debug.* library stripped)"
status: superseded
superseded_by: "MCP-001"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "Never call `debug.*`, `io.*`, or `os.*` in in-game Lua scripts. Modify or expose public table functions in override scripts instead."
---

# MCP-094: `kongor` / `kongor_game_lua` Pre-Execution Sandbox Guard Triggered (debug.* library stripped)

## Summary
Repeat occurrence of [`MCP-001`] on `kongor` / `kongor_game_lua` (`lua-sandbox`). Superseded by `MCP-001`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Retrieve upvalues of `CreateGame` via `debug.getupvalue`. |
| **Actual (Symptom)** | Blocked by pre-execution sandbox guard with category `LuaSandboxRestriction`: `K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine`. |
| **Root Cause** | In accordance with K2 engine design and `GEMINI.md`, the standard `debug`, `io`, and `os` libraries are stripped in the K2 Lua sandbox to prevent unsafe memory inspection or OS execution. |
| **Fix / Remediation** | Expose needed helper methods on public global tables in `override/ui/scripts/...`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local n, v = debug.getupvalue(Player_Hosted_V2.CreateGame, i)"
  }
  

Logs:

  K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Never call `debug.*`, `io.*`, or `os.*` in in-game Lua scripts. Modify or expose public table functions in override scripts instead.
