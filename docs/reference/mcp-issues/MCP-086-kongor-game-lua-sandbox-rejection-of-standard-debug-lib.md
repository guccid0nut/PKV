---
id: MCP-086
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "`kongor_game_lua` Sandbox Rejection of Standard `debug` Library"
status: superseded
superseded_by: "MCP-001"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "In K2 Lua scripts, avoid standard `debug.*` library calls. Use global table traversal (`pairs()`), `type()`, and widget hierarchy inspectors (`kongor_k2_ui_inspect`) to introspect script state."
---

# MCP-086: `kongor_game_lua` Sandbox Rejection of Standard `debug` Library

## Summary
Repeat occurrence of [`MCP-001`] on `kongor` / `kongor_game_lua` (`lua-sandbox`). Superseded by `MCP-001`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Introspecting function definition metadata via `debug.getinfo`. |
| **Actual (Symptom)** | Intercepted by `GameLuaHandler` pre-execution sandbox guard with `K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine... debug libraries are stripped`. |
| **Root Cause** | In K2 Lua engine runtime, the standard `debug`, `io`, and `os` libraries are stripped from root execution scope to protect engine stability. |
| **Fix / Remediation** | Introspect state via table keys and values using `pairs()` without calling `debug`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local info = debug.getinfo(Player_Hosted_V2.OnClickCreateGame)\nlocal out = {}\nfor k, v in pairs(info or {}) do\n    table.insert(out, tostring(k) .. \"=\" .. tostring(v))\nend\nreturn table.concat(out, \", \")"
  }
  

Logs:

  category: LuaSandboxRestriction
  errorMessage: K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
In K2 Lua scripts, avoid standard `debug.*` library calls. Use global table traversal (`pairs()`), `type()`, and widget hierarchy inspectors (`kongor_k2_ui_inspect`) to introspect script state.
