---
id: MCP-258
server: kongor
tool: kongor_client_eval_lua
category: lua-api
title: "K2 scrollbar widget lacks GetMaxValue method in EvalLua"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-02
last_seen: 2026-11-02
tags: [kongor, kongor_client_eval_lua, lua-api]
prevention_rule: "Read HoN_Options.maxPossibleScroll instead of calling GetMaxValue() on K2 scrollbar widgets; only GetValue() and SetMaxValue() are bound."
---

# MCP-258: K2 scrollbar widget lacks GetMaxValue method in EvalLua

## Summary
Calling `:GetMaxValue()` on a K2 `CSlider` / scrollbar widget (`options_scrollbar`) inside `kongor` / `kongor_client_eval_lua` fails with `caller_lua_error: attempt to call method 'GetMaxValue' (a nil value)` because K2's Lua widget metatable only binds `:GetValue()` and `:SetMaxValue()`. Agents inspecting scroll bounds in Options UI must read `HoN_Options.maxPossibleScroll` (or widget height deltas) rather than `:GetMaxValue()`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Inspecting `options_scrollbar` scroll position and max range in `game_options` via Lua eval returns numeric bounds. |
| **Actual (Symptom)** | `caller_lua_error`: `[string "kongor_eval"]:1: attempt to call method 'GetMaxValue' (a nil value)`. |
| **Root Cause** | K2's C++ `CSlider` Lua binding exposes `SetMaxValue(val)` and `GetValue()`, but does not register a `GetMaxValue()` getter on the widget userdata metatable. |
| **Fix / Remediation** | Query `:GetValue()` on the scrollbar widget and read `HoN_Options.maxPossibleScroll` (or adjust container `:SetY(...)` directly) instead of calling `:GetMaxValue()`. |

## Invocation Parameters & Raw Logs
```text
Tool: kongor_client_eval_lua
Arguments: { "action": "eval", "code": "local sb = UIManager.GetInterface('main'):GetWidget('options_scrollbar'); return sb:GetValue(), sb:GetMaxValue()" }
Error: caller_lua_error: [string "kongor_eval"]:1: attempt to call method 'GetMaxValue' (a nil value)
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Read HoN_Options.maxPossibleScroll instead of calling GetMaxValue() on K2 scrollbar widgets; only GetValue() and SetMaxValue() are bound.
