---
id: MCP-181
server: kongor
tool: kongor_game_client_manage
category: transport-eof
title: "KONGOR-MCP-WRAPPER-TIMEOUT (MCP-2026-10-05)"
status: superseded
superseded_by: "MCP-142"
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_game_client_manage, transport-eof]
prevention_rule: "Do not kill child `kongor.exe` without killing the parent Python transport bridge; terminate the parent wrapper or invoke direct CLI scripts."
---

# MCP-181: KONGOR-MCP-WRAPPER-TIMEOUT (MCP-2026-10-05)

## Summary
Repeat occurrence of [`MCP-142`] on `kongor` / `kongor_game_client_manage` (`transport-eof`). Superseded by `MCP-142`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Return client lifecycle status and active window mode. |
| **Actual (Symptom)** | Failed with error: `Encountered error in tool execution: MCP tool call to server "kongor" timed out after 3m0s: context deadline exceeded`. |
| **Root Cause** | When the underlying `kongor.exe` subprocess was killed while the parent Python stdio bridge process (`python3.12.exe` PID 40096) was still connected to Antigravity's stdio stream, the Python bridge blocked waiting for stdout without restarting `kongor.exe` or closing the transport. |
| **Fix / Remediation** | Do not kill child `kongor.exe` without killing the parent Python transport bridge; terminate the parent wrapper or invoke direct CLI scripts. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action":"status"}`
* **Expected**: Return client lifecycle status and active window mode.
* **Actual**: Failed with error: `Encountered error in tool execution: MCP tool call to server "kongor" timed out after 3m0s: context deadline exceeded`.
* **Root Cause**: When the underlying `kongor.exe` subprocess was killed while the parent Python stdio bridge process (`python3.12.exe` PID 40096) was still connected to Antigravity's stdio stream, the Python bridge blocked waiting for stdout without restarting `kongor.exe` or closing the transport.
* **Status**: `[RESOLVED]` (Terminated stale python wrapper process PID 40096 to allow Antigravity host to cleanly re-spawn the fresh MCP server instance, and used direct Windows PowerShell cmdlets `Get-Process hon_x64` / scripts for client supervision).
* **Next-Time Prevention Rule**: Do not kill child `kongor.exe` without killing the parent Python transport bridge; terminate the parent wrapper or invoke direct CLI scripts.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Do not kill child `kongor.exe` without killing the parent Python transport bridge; terminate the parent wrapper or invoke direct CLI scripts.
