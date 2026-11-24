---
id: MCP-148
server: kongor
tool: kongor_game_client_manage
category: transport-eof
title: "(formerly MCP-085): KONGOR-MCP-PROCESS-EOF-AFTER-KILL"
status: superseded
superseded_by: "MCP-142"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_game_client_manage, transport-eof]
prevention_rule: "Never kill the running MCP child process directly via `Stop-Process`. To test newly recompiled CLI features, invoke the CLI binary directly or run test suites."
---

# MCP-148: (formerly MCP-085): KONGOR-MCP-PROCESS-EOF-AFTER-KILL

## Summary
Repeat occurrence of [`MCP-142`] on `kongor` / `kongor_game_client_manage` (`transport-eof`). Superseded by `MCP-142`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Tool returns status JSON. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The MCP server process (`kongor.exe`) was manually stopped with `Stop-Process` while the IDE/agent session was running. The parent MCP stdio transport does not auto-reconnect once the child process's stdio stream closes with EOF. |
| **Fix / Remediation** | Never kill the running MCP child process directly via `Stop-Process`. To test newly recompiled CLI features, invoke the CLI binary directly or run test suites. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "status"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Never kill the running MCP child process directly via `Stop-Process`. To test newly recompiled CLI features, invoke the CLI binary directly or run test suites.
