---
id: MCP-193
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-TIMEOUT (MCP-2026-10-06)"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll`)."
---

# MCP-193: KONGOR-CLIENT-EVAL-LUA-TIMEOUT (MCP-2026-10-06)

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Lua code evaluated and result returned over IPC pipe `\\.\pipe\willowmaker-ipc` within 3000ms. |
| **Actual (Symptom)** | `Mailbox IPC request #310898991 timed out after 3000ms awaiting response from running client.` |
| **Root Cause** | The in-engine NamedPipe IPC handler is only pumped during `CHost::Frame()` when the client message queue is active. If the main game loop is rendering at very low FPS (~1.7 FPS) or waiting on engine state initialization, the 3000ms timeout can fire before the next tick polls the pipe. |
| **Fix / Remediation** | Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return tostring(_G.UIManager ~= nil)..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Patched in Step 6 (`EvalLuaSync` `0x000E` -> `0x000F` in `version.dll`).
