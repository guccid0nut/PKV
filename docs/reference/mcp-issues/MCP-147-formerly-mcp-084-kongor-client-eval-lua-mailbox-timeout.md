---
id: MCP-147
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "(formerly MCP-084): KONGOR-CLIENT-EVAL-LUA-MAILBOX-TIMEOUT-GAMEPHASE-0"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Verify kongor / kongor_client_eval_lua readiness and parameters before invocation."
---

# MCP-147: (formerly MCP-084): KONGOR-CLIENT-EVAL-LUA-MAILBOX-TIMEOUT-GAMEPHASE-0

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Lua code evaluated and result returned over `\\.\pipe\willowmaker-ipc` within 3000ms. |
| **Actual (Symptom)** | `Mailbox IPC request #310898997 timed out after 3000ms awaiting response from running client.` |
| **Root Cause** | In `GamePhase 0` (pre-login state), the nexus mailbox frame watcher (`nexus_mailbox_frame_watcher`) may not have its `onframe` loop registered if the active interface is transitioning, causing mailbox messages to queue without being pumped in 3000ms. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return 'PING FROM LUA'"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Verify kongor / kongor_client_eval_lua readiness and parameters before invocation.
