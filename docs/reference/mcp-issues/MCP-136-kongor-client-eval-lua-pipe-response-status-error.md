---
id: MCP-136
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-PIPE-RESPONSE-STATUS-ERROR"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: ["MCP-193", "MCP-147", "MCP-195", "MCP-208", "MCP-220", "MCP-221", "MCP-225"]
occurrences: 29
date: 2026-10-13
last_seen: 2026-11-03
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`): `kongor_client_eval_lua` now evaluates Lua synchronously in 1 frame (~3ms) and returns the exact evaluated string."
---

# MCP-136: KONGOR-CLIENT-EVAL-LUA-PIPE-RESPONSE-STATUS-ERROR

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `ipc-timeout`, status: `investigating`, occurrences: 29). Return evaluated string from `hon_x64.exe` Lua state. -> Returned `{"status": "error", "success": false, "response": "{\"success\":true,\"command\":\"Script ...\"}"}`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Return evaluated string from `hon_x64.exe` Lua state. |
| **Actual (Symptom)** | Returned `{"status": "error", "success": false, "response": "{\"success\":true,\"command\":\"Script ...\"}"}`. |
| **Root Cause** | `kongor_client_eval_lua` dispatches via `willowmaker-ipc` fire-and-forget `Script "..."` wrapper (`Echo('[LUA_OK] ...')`) which returns command-submission acknowledgment JSON `{"success":true,"command":...}` rather than synchronous return value payload, and its parser treats absence of an inline `[LUA_OK]` string in the immediate ACK as `success: f... |
| **Fix / Remediation** | Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`): `kongor_client_eval_lua` now evaluates Lua synchronously in 1 frame (~3ms) and returns the exact evaluated string. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "local count = (HoN_Store and HoN_Store.HeroAvatars and #HoN_Store.HeroAvatars) or 0; return 'HeroAvatars=' .. tostring(count) .. ' currentCategory=' .. tostring(HoN_Store and HoN_Store.currentCategory)"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll` and `ClientEvalLuaHandler.cs`): `kongor_client_eval_lua` now evaluates Lua synchronously in 1 frame (~3ms) and returns the exact evaluated string.
