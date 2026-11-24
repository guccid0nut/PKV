---
id: MCP-152
server: kongor
tool: kongor_client_eval_lua
category: transport-eof
title: "(formerly MCP-089): KONGOR-CLIENT-EVAL-LUA-EOF-ON-CLOSED-TRANSPORT"
status: superseded
superseded_by: "MCP-149"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_client_eval_lua, transport-eof]
prevention_rule: "Verify kongor / kongor_client_eval_lua readiness and parameters before invocation."
---

# MCP-152: (formerly MCP-089): KONGOR-CLIENT-EVAL-LUA-EOF-ON-CLOSED-TRANSPORT

## Summary
Repeat occurrence of [`MCP-149`] on `kongor` / `kongor_client_eval_lua` (`transport-eof`). Superseded by `MCP-149`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Dispatches eval expression over willowmaker-ipc named pipe. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The `kongor` MCP server child process terminated or disconnected its stdio transport channel earlier. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return 'pong'"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Verify kongor / kongor_client_eval_lua readiness and parameters before invocation.
