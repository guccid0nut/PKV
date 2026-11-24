---
id: MCP-180
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "CLIENT-EVAL-LUA-SEMICOLON (MCP-2026-10-05)"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "All K2 console `Script` invocations generated in C# handlers must be enclosed in quotes and strictly avoid semicolons, using spaces or newlines instead."
---

# MCP-180: CLIENT-EVAL-LUA-SEMICOLON (MCP-2026-10-05)

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Execute Lua statement and return string representation of active interface. |
| **Actual (Symptom)** | Failed with error: `Encountered error in tool execution: {"status":"error","success":false,"action":"eval","response":"{\"success\":true,\"command\":\"Script local _ok, _res = pcall(loadstring(\\\"return tostring(UIManager.GetActiveInterface())\\\")); if not _ok then Echo('[LUA_ERR] ' .. tostring(_r... |
| **Root Cause** | `ClientEvalLuaHandler.cs` constructed K2 console commands with unquoted semicolons (`;`) separating statements (`pcall(...); if ...; else ...; end`). In K2 console parser, unquoted semicolons terminate commands and split the payload into invalid separate commands. Additionally, the payload following `Script` was not enclosed in outer quotation mark... |
| **Fix / Remediation** | All K2 console `Script` invocations generated in C# handlers must be enclosed in quotes and strictly avoid semicolons, using spaces or newlines instead. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action":"eval","code":"return tostring(UIManager.GetActiveInterface())"}`
* **Expected**: Execute Lua statement and return string representation of active interface.
* **Actual**: Failed with error: `Encountered error in tool execution: {"status":"error","success":false,"action":"eval","response":"{\"success\":true,\"command\":\"Script local _ok, _res = pcall(loadstring(\\\"return tostring(UIManager.GetActiveInterface())\\\")); if not _ok then Echo('[LUA_ERR] ' .. tostring(_res)) else Echo('[LUA_OK] ' .. tostring(_res)) end\"}"}`.
* **Root Cause**: `ClientEvalLuaHandler.cs` constructed K2 console commands with unquoted semicolons (`;`) separating statements (`pcall(...); if ...; else ...; end`). In K2 console parser, unquoted semicolons terminate commands and split the payload into invalid separate commands. Additionally, the payload following `Script` was not enclosed in outer quotation marks.
* **Status**: `[PATCHED]` (Fixed in `ClientEvalLuaHandler.cs`: removed semicolons from statement separators, sanitized code input, and safely wrapped entire script payload in quotes `Script "..."`).
* **Next-Time Prevention Rule**: All K2 console `Script` invocations generated in C# handlers must be enclosed in quotes and strictly avoid semicolons, using spaces or newlines instead.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
All K2 console `Script` invocations generated in C# handlers must be enclosed in quotes and strictly avoid semicolons, using spaces or newlines instead.
