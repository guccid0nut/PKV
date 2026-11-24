---
id: MCP-265
server: kongor
tool: kongor_game_action
category: command-dispatch
title: "move_to emits 'Order move' rejected by CConsole::DoCommand"
status: resolved
verified_by: "f3c7add47 + McpToolContractTests.Tranche2 + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-09
last_seen: 2026-11-17
tags: [kongor, kongor_game_action, command-dispatch]
prevention_rule: "Do not rely on kongor_game_action(action: 'move_to') over client console IPC because K2 CConsole::DoCommand rejects 'Order'; drive bot/unit movement via CBotBrain Lua or server scripts."
---

# MCP-265: move_to emits 'Order move' rejected by CConsole::DoCommand

## Summary
Calling `kongor_game_action(action: 'move_to', destination: 'client', x: 4096, y: 5830)` returns HTTP/IPC `"status": "success"`, but inside `override/console.log` K2's console parser logs `CConsole::DoCommand() - Unknown identifier: 'Order'` without moving the local hero.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_action` |
| **Expected** | Local controlled hero moves to world coordinates `(4096, 5830)`. |
| **Actual (Symptom)** | Tool returns `{"status":"success","command":"Order move 4096.0 5830.0"}`, while `console.log` logs `CConsole::DoCommand() - Unknown identifier: 'Order'` and hero remains stationary. |
| **Root Cause** | `GameActionHandler` formats `move_to` as a raw K2 console string `Order move <x> <y>`, which is not a registered K2 `CConsole` command on `hon_x64.exe` client builds. |
| **Fix / Remediation** | Handle AFK/idle local heroes directly inside `CBotBrain` Lua (`bAfkHumanCarrier` rescue after `>8.5s` motionless) instead of dispatching `Order move` over client console IPC. |

## Invocation Parameters & Raw Logs
```text
{"action":"move_to","destination":"client","x":4096,"y":5830}
[20:53:42] CConsole::DoCommand() - Unknown identifier: 'Order'
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_action
```

## Prevention Invariant
Do not rely on kongor_game_action(action: 'move_to') over client console IPC because K2 CConsole::DoCommand rejects 'Order'; drive bot/unit movement via CBotBrain Lua or server scripts.
