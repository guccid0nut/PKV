---
id: MCP-087
server: kongor
tool: kongor_game_lua
category: lua-sandbox
title: "`kongor_game_lua` Sandbox Rejection of `debug.getupvalue` Introspection"
status: superseded
superseded_by: "MCP-001"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, lua-sandbox]
prevention_rule: "Do not use `debug.getupvalue` or any `debug.*` calls in `kongor_game_lua`. Inspect state via `UIManager.GetInterface('main')`, global module tables, or console logs."
---

# MCP-087: `kongor_game_lua` Sandbox Rejection of `debug.getupvalue` Introspection

## Summary
Repeat occurrence of [`MCP-001`] on `kongor` / `kongor_game_lua` (`lua-sandbox`). Superseded by `MCP-001`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Introspecting closure upvalues of `CreateGame`. |
| **Actual (Symptom)** | Intercepted by `GameLuaHandler` sandbox pre-execution guard (`category: LuaSandboxRestriction`). |
| **Root Cause** | The K2 in-engine Lua sandbox strips the standard `debug` library (`debug` is nil in engine runtime), and `GameLuaHandler` regex guards detect and reject `debug.` calls before sending to the engine. |
| **Fix / Remediation** | Queried interface and global objects directly via `UIManager.GetInterface('main')`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local i = 1\nlocal s = \"\"\nwhile true do\n    local name, val = debug.getupvalue(Player_Hosted_V2.CreateGame, i)\n    if not name then break end\n    s = s .. name .. \"=\" .. tostring(val) .. \"; \"\n    i = i + 1\nend\nreturn s"
  }
  

Logs:

  category: LuaSandboxRestriction
  errorMessage: K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine ('io' is nil)...
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Do not use `debug.getupvalue` or any `debug.*` calls in `kongor_game_lua`. Inspect state via `UIManager.GetInterface('main')`, global module tables, or console logs.
