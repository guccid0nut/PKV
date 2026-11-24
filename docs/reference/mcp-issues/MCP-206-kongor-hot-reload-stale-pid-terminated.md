---
id: MCP-206
server: kongor
tool: kongor_hot_reload
category: client-offline
title: "KONGOR-HOT-RELOAD-STALE-PID-TERMINATED"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_hot_reload, client-offline]
prevention_rule: "Verify `Get-Process hon_x64` or `kongor_game_client_manage` (`action: 'status'`) before invoking `kongor_hot_reload`, and launch the client if not running."
---

# MCP-206: KONGOR-HOT-RELOAD-STALE-PID-TERMINATED

## Summary
Tracked MCP issue on `kongor` / `kongor_hot_reload` (category: `client-offline`, status: `resolved`, occurrences: 1). Trigger `ReloadInterfaces` on running `hon_x64.exe` process. -> Returned `Game process (PID: 32460) terminated unexpectedly with exit code 0.` because the user had already closed the client (`PID 32460`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_hot_reload` |
| **Expected** | Trigger `ReloadInterfaces` on running `hon_x64.exe` process. |
| **Actual (Symptom)** | Returned `Game process (PID: 32460) terminated unexpectedly with exit code 0.` because the user had already closed the client (`PID 32460`). |
| **Root Cause** | `PID 32460` from an earlier session had already exited cleanly (`exit code 0`), so no live `hon_x64.exe` process was listening on the IPC pipe. |
| **Fix / Remediation** | Verify `Get-Process hon_x64` or `kongor_game_client_manage` (`action: "status"`) before invoking `kongor_hot_reload`, and launch the client if not running. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "trigger"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_hot_reload
```

## Prevention Invariant
Verify `Get-Process hon_x64` or `kongor_game_client_manage` (`action: 'status'`) before invoking `kongor_hot_reload`, and launch the client if not running.
