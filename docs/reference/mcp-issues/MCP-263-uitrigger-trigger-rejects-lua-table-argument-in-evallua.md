---
id: MCP-263
server: kongor
tool: kongor_client_eval_lua
category: lua-eval
title: "UITrigger:Trigger rejects Lua table argument in EvalLua"
status: investigating
codified_in: "src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs"
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 9
date: 2026-11-08
last_seen: 2026-11-18
tags: [kongor, kongor_client_eval_lua, lua-eval, uitrigger]
prevention_rule: "Pass individual string arguments (e.g. t:Trigger('6', '3', 'true')) to K2 UITrigger:Trigger(...) rather than wrapping parameters in a Lua table."
---

# MCP-263: UITrigger:Trigger rejects Lua table argument in EvalLua

## Summary
When invoking `kongor` / `kongor_client_eval_lua` (`action: 'eval'`) to fire a K2 `UITrigger` such as `revertUITrigger`, passing a Lua table (`t:Trigger({'6', '3', 'true'})`) raises `bad argument #1 to 'Trigger' (string expected, got table)` because K2's C++ `CUITrigger::Trigger` Lua binding reads positional string varargs (`luaL_checkstring`) rather than a Lua table.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | `UITrigger.GetTrigger('revertUITrigger'):Trigger(...)` switches `game_options` to category `6`, subcategory `3`. |
| **Actual (Symptom)** | `[ERROR] bad argument #1 to 'Trigger' (string expected, got table)` (`errorCode: "luaEvalError"`). |
| **Root Cause** | Unlike legacy `LuaTrigger` table-based helpers, K2's native `UITrigger:Trigger(...)` C++ binding expects positional string arguments (`param0, param1, param2`). |
| **Fix / Remediation** | Call `t:Trigger('6', '3', 'true')` with individual string arguments. |

## Invocation Parameters & Raw Logs
```text
{"action":"eval","code":"local t = UITrigger.GetTrigger('revertUITrigger') if t then t:Trigger({'6', '3', 'true'}) end"}
-> [ERROR] bad argument #1 to 'Trigger' (string expected, got table)
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Pass individual string arguments (e.g. t:Trigger('6', '3', 'true')) to K2 UITrigger:Trigger(...) rather than wrapping parameters in a Lua table.
