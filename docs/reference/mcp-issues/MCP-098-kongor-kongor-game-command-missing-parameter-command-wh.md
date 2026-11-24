---
id: MCP-098
server: kongor
tool: kongor_game_command
category: parameter-mismatch
title: "`kongor` / `kongor_game_command` Missing Parameter 'command' when passing 'code'"
status: superseded
superseded_by: "MCP-092"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_command, parameter-mismatch]
prevention_rule: "For `kongor_game_command`, always supply `command: '<command_string>'`."
---

# MCP-098: `kongor` / `kongor_game_command` Missing Parameter 'command' when passing 'code'

## Summary
Repeat occurrence of [`MCP-092`] on `kongor` / `kongor_game_command` (`parameter-mismatch`). Superseded by `MCP-092`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Execute console command in game client. |
| **Actual (Symptom)** | Returned error: `Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').` |
| **Root Cause** | `kongor_game_command` expects the argument `command: "<string>"` rather than `code: "<string>"` (which is used by `kongor_game_lua`). |
| **Fix / Remediation** | For `kongor_game_command`, always supply `command: "<command_string>"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "cvarlist *timeout*"
  }
  

Logs:

  Encountered error in tool execution: Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
For `kongor_game_command`, always supply `command: '<command_string>'`.
