---
id: MCP-119
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "KONGOR-CLIENT-EVAL-LUA-UNPARSED-RESPONSE-PAYLOAD"
status: resolved
verified_by: "McpToolContractTests.MCP002_And_MCP051_And_MCP119_LuaInterfaceRemediation_PidWindowTargeting_AndSyncEvalPayload"
superseded_by: ""
supersedes: ["MCP-180", "MCP-146", "MCP-162", "MCP-163", "MCP-126", "MCP-202", "MCP-203"]
occurrences: 8
date: 2026-10-10
last_seen: 2026-10-14
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`)."
---

# MCP-119: KONGOR-CLIENT-EVAL-LUA-UNPARSED-RESPONSE-PAYLOAD

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `runtime-error`, status: `patched`, occurrences: 8). Evaluates Lua snippet and returns parsed boolean result. -> Returns error status because response payload is formatted as raw console command `Script "..."` without standard JSON response decoding.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluates Lua snippet and returns parsed boolean result. |
| **Actual (Symptom)** | Returns error status because response payload is formatted as raw console command `Script "..."` without standard JSON response decoding. |
| **Root Cause** | `ClientEvalLuaHandler` wraps expression in `Script "..."` and fails to unpack the result from the client log stream. |
| **Fix / Remediation** | Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"action": "eval", "code": "return GetWidget('login_hero_viewport') ~= nil"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`).
