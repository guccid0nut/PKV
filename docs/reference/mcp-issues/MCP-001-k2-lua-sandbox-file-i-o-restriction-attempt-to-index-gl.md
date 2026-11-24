---
id: MCP-001
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "K2 Lua Sandbox File I/O Restriction (Attempt to Index Global 'io')"
status: patched
verified_by: "ClientCommandRemediationTests (commit 5e7ff0adb)"
superseded_by: ""
supersedes: ["MCP-019", "MCP-029", "MCP-086", "MCP-087", "MCP-094"]
occurrences: 6
date: 2026-09-23
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "Pre-execution regex guard in `GameLuaHandler` blocks forbidden `io.*`/`os.*` before client dispatch."
---

# MCP-001: K2 Lua Sandbox File I/O Restriction (Attempt to Index Global 'io')

## Summary
Tracked MCP issue on `kongor` / `kongor_game_lua` (category: `lua-sandbox`, status: `patched`, occurrences: 6). Game client writes engine version variables to local disk file. -> Command threw Lua runtime error: `attempt to index global 'io' (a nil value)`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Game client writes engine version variables to local disk file. |
| **Actual (Symptom)** | Command threw Lua runtime error: `attempt to index global 'io' (a nil value)`. |
| **Root Cause** | The K2 engine embeds a sandboxed Lua environment that strips dangerous standard library packages (`io`, `os`, `package`, `loadfile`) to prevent untrusted game scripts and mods from modifying the host filesystem or executing arbitrary code. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameLuaHandler.cs`: Added pre-execution regex sandbox inspection that catches `\bio\.(open/popen/lines/input/output)\b` and `\bos\.(execute/remove/rename)\b` prior to dispatching to the client. Returns structured error `LuaSandboxRestriction` with actionable guidance (`Echo()`, cvars, or native tools... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  Script local f = io.open('C:/ProjectKongor/logs/k2_version.txt', 'w'); if f then f:write(tostring(GetCvarString('version')) .. '\n' .. tostring(GetCvarString('host_version')) .. '\n' .. tostring(GetCvarString('svr_version'))); f:close(); Echo('Wrote version to file') else Echo('Failed to open file') end
  

Logs:

  [ERROR] [string "local f = io.open('C:/ProjectKongor/logs/k2..."]:1: attempt to index global 'io' (a nil value)
  client_command_errors.ndjson: Id 8b512bdc, ErrorCategory: ExecutionError
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Pre-execution regex guard in `GameLuaHandler` blocks forbidden `io.*`/`os.*` before client dispatch.
