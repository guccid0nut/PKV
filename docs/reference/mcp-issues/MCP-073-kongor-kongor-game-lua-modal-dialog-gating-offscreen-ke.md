---
id: MCP-073
server: kongor
tool: kongor_game_lua
category: window-resolution
title: "`kongor` kongor_game_lua Modal Dialog Gating & Offscreen Keystroke Suppression"
status: superseded
superseded_by: "MCP-051"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, window-resolution]
prevention_rule: "Dismiss modal dialogs before querying Lua or route via mailbox IPC."
---

# MCP-073: `kongor` kongor_game_lua Modal Dialog Gating & Offscreen Keystroke Suppression

## Summary
Repeat occurrence of [`MCP-051`] on `kongor` / `kongor_game_lua` (`window-resolution`). Superseded by `MCP-051`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Lua script executes in client VM and returns watermark cvar string. |
| **Actual (Symptom)** | Failed with `ExecutionError` in `in_game_console` channel: `Window is offscreen (headless mode). Keystroke simulation is disabled to prevent desktop interference.` |
| **Root Cause** | The client had an active UI modal dialog ("Error: No response from server") open, which intercepted the mailbox IPC polling loop and triggered the fallback cascade to `in_game_console`. The offscreen guardrail correctly prevented virtual keyboard manipulation to ensure 0% desktop disruption. |
| **Fix / Remediation** | Dismiss modal dialog and route commands directly through mailbox IPC. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "return tostring(GetCvar('net_enableWatermark') and GetCvar('net_enableWatermark'):GetString() or 'nil')"
  }
  

Logs:

  {
    "status": "error",
    "target": "client",
    "channel": "in_game_console",
    "errorMessage": "Window is offscreen (headless mode). Keystroke simulation is disabled to prevent desktop interference. Route commands via Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) or switch window mode to foreground.",
    "executionDurationMs": 7352.08
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Dismiss modal dialogs before querying Lua or route via mailbox IPC.
