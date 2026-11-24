---
id: MCP-036
server: kongor
tool: kongor_game_command
category: window-resolution
title: "Mailbox IPC Timeout and Keystroke Fallback Suppressed Due to D3D9 Device Query Stall"
status: superseded
superseded_by: "MCP-003"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_command, window-resolution]
prevention_rule: "Do not use transient window repositioning on D3D9/DXVK headless clients; prioritize Vulkan swapchain capture."
---

# MCP-036: Mailbox IPC Timeout and Keystroke Fallback Suppressed Due to D3D9 Device Query Stall

## Summary
Repeat occurrence of [`MCP-003`] on `kongor` / `kongor_game_command` (`window-resolution`). Superseded by `MCP-003`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Game client receives and executes console command `Connect 127.0.0.1:11435` via mailbox IPC. |
| **Actual (Symptom)** | Failed with `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` (`KeystrokeFallbackSuppressed`). |
| **Root Cause** | `kongor_transient_capture` momentarily repositioned the offscreen client window to `(0, 0)` and restored it to `(-32000, -32000)`. Because D3D9/DXVK render thread was presenting, the instantaneous repositioning triggered a rapid sequence of `Vid: Query not S_OK` device queries in the client engine. The render thread stalled, starving the Lua heartb... |
| **Fix / Remediation** | If a D3D9 query stall occurs after transient repositioning, terminate and relaunch the client cleanly without desktop movement. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "command",
    "command": "Connect 127.0.0.1:11435"
  }
  

Logs:

  Encountered error in tool execution: {
    "status": "error",
    "target": "client",
    "channel": "none",
    "command": "Connect 127.0.0.1:11435",
    "errorMessage": "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.",
    "remediationCategory": "KeystrokeFallbackSuppressed"
  }
  Vid: [02:32:22] Query not S_OK
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Do not use transient window repositioning on D3D9/DXVK headless clients; prioritize Vulkan swapchain capture.
