---
id: MCP-019
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "K2 In-Engine Lua Sandbox Rejection of Standard `io` Library"
status: superseded
superseded_by: "MCP-001"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "Pre-execution regex guard in `GameLuaHandler` blocks `io.*` before client dispatch."
---

# MCP-019: K2 In-Engine Lua Sandbox Rejection of Standard `io` Library

## Summary
Repeat occurrence of [`MCP-001`] on `kongor` / `kongor_game_lua` (`lua-sandbox`). Superseded by `MCP-001`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Lua script creates/writes a diagnostic file via `io.open`. |
| **Actual (Symptom)** | Failed with `[ERROR] attempt to index global 'io' (a nil value)`. |
| **Root Cause** | The K2 game engine Lua environment intentionally strips and sandboxes the standard Lua `io` and `os` libraries to prevent arbitrary filesystem modifications from untrusted UI scripts and mods. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameLuaHandler.cs`: Pre-execution regex inspection intercepts `io.*` calls before sending to the client, returning structured `LuaSandboxRestriction` error and preventing engine crashes. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "local f = io.open('Player_Hosted_V2_methods.txt', 'w') ...",
    "target": "client"
  }
  

Logs:

  [ERROR] [string "local found = {}..."]:8: attempt to index global 'io' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Pre-execution regex guard in `GameLuaHandler` blocks `io.*` before client dispatch.
