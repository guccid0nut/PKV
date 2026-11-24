---
id: MCP-072
server: kongor
tool: kongor_game_command
category: window-resolution
title: "`kongor` kongor_game_command KeystrokeFallbackSuppressed IPC Unavailable"
status: superseded
superseded_by: "MCP-003"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_command, window-resolution]
prevention_rule: "Route commands via `kongor_game_lua` (`Cmd(...)`) or active mailbox IPC."
---

# MCP-072: `kongor` kongor_game_command KeystrokeFallbackSuppressed IPC Unavailable

## Summary
Repeat occurrence of [`MCP-003`] on `kongor` / `kongor_game_command` (`window-resolution`). Superseded by `MCP-003`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Command dispatched to game client via IPC or console stream. |
| **Actual (Symptom)** | Failed with `KeystrokeFallbackSuppressed`: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` |
| **Root Cause** | The player client was launched in headless offscreen mode without the active Willowmaker/Nexus mailbox IPC loop configured, and virtual keyboard simulation was safely suppressed by the engine guardrail to prevent desktop disruption. Direct console input routes through `kongor_game_lua` or the mailbox IPC file in `userdataKONGOR/extensions/nexus_ipc... |
| **Fix / Remediation** | Dispatched connection command via `kongor_game_lua` (`Cmd("Connect 127.0.0.1:11235")`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`json
  {
    "command": "Connect 127.0.0.1:11235"
  }
  `

Logs:
`json
  {
    "status": "error",
    "target": "client",
    "channel": "none",
    "command": "Connect 127.0.0.1:11235",
    "errorMessage": "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.",
    "remediationCategory": "KeystrokeFallbackSuppressed",
    "remediationHint": "Ensure game client is active with Nexus Mailbox IPC enabled in userdataKONGOR/extensions/nexus_ipc."
  }
  `
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Route commands via `kongor_game_lua` (`Cmd(...)`) or active mailbox IPC.
