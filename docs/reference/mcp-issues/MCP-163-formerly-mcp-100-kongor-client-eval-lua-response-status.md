---
id: MCP-163
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "(formerly MCP-100): KONGOR-CLIENT-EVAL-LUA-RESPONSE-STATUS-MISMATCH"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) synchronous 1-frame evaluation receipt."
---

# MCP-163: (formerly MCP-100): KONGOR-CLIENT-EVAL-LUA-RESPONSE-STATUS-MISMATCH

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Returns JSON payload indicating success and evaluation output. |
| **Actual (Symptom)** | Outer envelope reported `status: error`, `success: false` despite inner IPC `response: {"success": true, ...}` successfully dispatching to the client and `[LUA_OK] Hello from Kongor Lua` being printed in the engine console. |
| **Root Cause** | The MCP server implementation flags the envelope as error if it does not receive an async return message from the client pipe, even though the command dispatched and executed synchronously on `hon_x64.exe`. |
| **Fix / Remediation** | Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) synchronous 1-frame evaluation receipt. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return 'Hello from Kongor Lua'"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) synchronous 1-frame evaluation receipt.
