---
id: MCP-207
server: kongor
tool: kongor_wait_for
category: ipc-timeout
title: "KONGOR-WAIT-FOR-COLD-START-TIMEOUT"
status: superseded
superseded_by: "MCP-196"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_wait_for, ipc-timeout]
prevention_rule: "Poll `kongor_game_client_manage` (`action: 'logs'`) or use a 35s+ timeout window on cold launch before sending IPC commands."
---

# MCP-207: KONGOR-WAIT-FOR-COLD-START-TIMEOUT

## Summary
Repeat occurrence of [`MCP-196`] on `kongor` / `kongor_wait_for` (`ipc-timeout`). Superseded by `MCP-196`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | Return `satisfied: true` once `hon_x64.exe` finishes cold-booting `main` UI. |
| **Actual (Symptom)** | Timed out at 25.9s while `hon_x64.exe` was still mounting `.s2z` archives and compiling `main_v2.interface`. |
| **Root Cause** | Cold-booting `hon_x64.exe` and parsing `main_v2.interface` takes ~30–35 seconds before the IPC pipe begins servicing queries. |
| **Fix / Remediation** | Poll `kongor_game_client_manage` (`action: "logs"`) or use a 35s+ timeout window on cold launch before sending IPC commands. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"condition": "engine_ready", "timeoutMs": 25000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
Poll `kongor_game_client_manage` (`action: 'logs'`) or use a 35s+ timeout window on cold launch before sending IPC commands.
