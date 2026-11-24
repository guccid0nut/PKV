---
id: MCP-108
server: kongor
tool: kongor_game_lua
category: window-resolution
title: "`kongor` / `kongor_game_lua` Keystroke Fallback Suppressed on Mailbox IPC Timeout for Target `client:17424`"
status: superseded
superseded_by: "MCP-051"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_lua, window-resolution]
prevention_rule: "For multi-client environments sharing `userdata`, ensure dedicated per-slot pipe or allow sufficient timeout and verify client mailbox handler readiness."
---

# MCP-108: `kongor` / `kongor_game_lua` Keystroke Fallback Suppressed on Mailbox IPC Timeout for Target `client:17424`

## Summary
Repeat occurrence of [`MCP-051`] on `kongor` / `kongor_game_lua` (`window-resolution`). Superseded by `MCP-051`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Return evaluated Lua snippet from targeted client PID 17424. |
| **Actual (Symptom)** | Failed with `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` after 3000ms timeout. |
| **Root Cause** | `GameClientManageHandler.ExecuteClientCommandAsync` disables keystroke fallback when an explicit PID target is given (`allowKeystrokeFallback = false`) to prevent accidental foreground keystrokes. When the secondary client does not consume the mailbox request within the timeout window, the call fails. |
| **Fix / Remediation** | Rely on visual capture or direct console verification. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "target": "client:17424",
    "code": "local t = GetTrigger('AccountInfo'); if t then return tostring(t.nickname) else return 'no trigger' end"
  }
  

Logs:

  "errorMessage": "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft."
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
For multi-client environments sharing `userdata`, ensure dedicated per-slot pipe or allow sufficient timeout and verify client mailbox handler readiness.
