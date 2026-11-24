---
id: MCP-092
server: kongor
tool: kongor_game_command
category: parameter-mismatch
title: "`kongor` / `kongor_game_command` Parameter Validation: Missing Required Parameter 'command'"
status: documented
superseded_by: ""
supersedes: ["MCP-098"]
occurrences: 2
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_command, parameter-mismatch]
prevention_rule: "Always provide a non-empty `command` parameter when invoking `kongor_game_command`, or use `kongor_game_client_manage action: 'status'` / `'errors'` for diagnostic queries."
---

# MCP-092: `kongor` / `kongor_game_command` Parameter Validation: Missing Required Parameter 'command'

## Summary
Tracked MCP issue on `kongor` / `kongor_game_command` (category: `parameter-mismatch`, status: `resolved`, occurrences: 2). Retrieve console log output without providing a command. -> Failed with `Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Retrieve console log output without providing a command. |
| **Actual (Symptom)** | Failed with `Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').` |
| **Root Cause** | `kongor_game_command` requires the `command` parameter to be non-empty for execution commands. For status/errors without dispatching a command, `kongor_game_client_manage` or `action: "status"` should be used. |
| **Fix / Remediation** | Always provide a non-empty `command` parameter when invoking `kongor_game_command`, or use `kongor_game_client_manage action: "status"` / `"errors"` for diagnostic queries. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "console"
  }
  

Logs:

  Encountered error in tool execution: Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Always provide a non-empty `command` parameter when invoking `kongor_game_command`, or use `kongor_game_client_manage action: 'status'` / `'errors'` for diagnostic queries.
