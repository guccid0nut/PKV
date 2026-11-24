---
id: MCP-115
server: kongor
tool: kongor_capture_window
category: lua-sandbox
title: "`kongor` / `kongor_capture_window` Access Denied: Unmanaged Process 77548"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-09-26
last_seen: 2026-10-16
tags: [kongor, kongor_capture_window, lua-sandbox]
prevention_rule: "Always spawn game clients through `kongor_game_client_manage` (action: 'launch') or `kongor client launch` so the PID is registered in `ProcessRegistry`."
---

# MCP-115: `kongor` / `kongor_capture_window` Access Denied: Unmanaged Process 77548

## Summary
Tracked MCP issue on `kongor` / `kongor_capture_window` (category: `lua-sandbox`, status: `resolved`, occurrences: 1). Capture frame from Client 2 process PID 77548. -> Failed with `Access denied: Process 77548 is not a managed test client. Agents are prohibited from interacting with unmanaged processes or the user's personal game session.`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Capture frame from Client 2 process PID 77548. |
| **Actual (Symptom)** | Failed with `Access denied: Process 77548 is not a managed test client. Agents are prohibited from interacting with unmanaged processes or the user's personal game session.`. |
| **Root Cause** | `ProcessRegistry` security sandbox requires client processes to be launched via `kongor_game_client_manage` or `kongor client launch` so they are registered with `AgentOwner`. Manually spawned processes via Win32 WMI are treated as unmanaged personal user sessions to prevent unauthorized window inspection. |
| **Fix / Remediation** | Launch client instances through `kongor_game_client_manage` tool. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "auto",
    "target": "pid:77548"
  }
  

Logs:

  Access denied: Process 77548 is not a managed test client. Agents are prohibited from interacting with unmanaged processes or the user's personal game session.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Always spawn game clients through `kongor_game_client_manage` (action: 'launch') or `kongor client launch` so the PID is registered in `ProcessRegistry`.
