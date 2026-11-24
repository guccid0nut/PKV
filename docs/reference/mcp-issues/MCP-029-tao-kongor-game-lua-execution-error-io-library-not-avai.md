---
id: MCP-029
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "TAO `kongor_game_lua` Execution Error: `io` Library Not Available in Sandboxed K2 Lua Environment"
status: superseded
superseded_by: "MCP-001"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "Pre-execution regex guard in `GameLuaHandler` blocks `io.*`/`os.*` before client dispatch."
---

# MCP-029: TAO `kongor_game_lua` Execution Error: `io` Library Not Available in Sandboxed K2 Lua Environment

## Summary
Repeat occurrence of [`MCP-001`] on `kongor` / `kongor_game_lua` (`lua-sandbox`). Superseded by `MCP-001`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Lua script opens a debug output file via standard Lua `io` library. |
| **Actual (Symptom)** | Returned error: `attempt to index global 'io' (a nil value)`. |
| **Root Cause** | The K2 game engine runs a secured and sandboxed Lua state where file I/O libraries (`io`, `os`) are stripped from the global execution environment. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameLuaHandler.cs`: Added pre-execution regex inspection that catches `\bio\.(open/popen/lines/input/output)\b` and `\bos\.(execute/remove/rename)\b` prior to dispatching to the client. Returns structured error `LuaSandboxRestriction` with actionable alternatives (`Echo()`, cvars, or native tools), p... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "target": "client",
    "code": "local f = io.open(\"...\")"
  }
  

Logs:

  [ERROR] attempt to index global 'io' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Pre-execution regex guard in `GameLuaHandler` blocks `io.*`/`os.*` before client dispatch.
