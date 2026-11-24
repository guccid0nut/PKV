---
id: MCP-126
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "KONGOR-CLIENT-EVAL-LUA-PIPE-BROKEN"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Patched in Step 5 & Step 6 (`version.dll` multi-client pipe + `EvalLuaSync` `0x000E` -> `0x000F`)."
---

# MCP-126: KONGOR-CLIENT-EVAL-LUA-PIPE-BROKEN

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Lua evaluation result from the running client IPC. |
| **Actual (Symptom)** | "Pipe is broken" error. |
| **Root Cause** | Named pipe IPC channel between Willowmaker/Kongor MCP host and `hon_x64` was not actively connected or broken when the client process was spawned outside Willowmaker. |
| **Fix / Remediation** | Patched in Step 5 & Step 6 (`version.dll` multi-client pipe + `EvalLuaSync` `0x000E` -> `0x000F`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"code": "return tostring(_G._empathCycleRunning)..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Patched in Step 5 & Step 6 (`version.dll` multi-client pipe + `EvalLuaSync` `0x000E` -> `0x000F`).
