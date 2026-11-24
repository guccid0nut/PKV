---
id: MCP-176
server: tao
tool: kongor_game_client_manage
category: runtime-error
title: "(formerly MCP-113): TAO-MCP-SERVER-PERMISSION-OR-TOOL-NOT-ENABLED"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [tao, kongor_game_client_manage, runtime-error]
prevention_rule: "Manage client lifecycle via PowerShell processes and scripts directly."
---

# MCP-176: (formerly MCP-113): TAO-MCP-SERVER-PERMISSION-OR-TOOL-NOT-ENABLED

## Summary
Tracked MCP issue on `tao` / `kongor_game_client_manage` (category: `runtime-error`, status: `resolved`, occurrences: 1). Returns client status via TAO MCP. -> Error: tool kongor_game_client_manage is not enabled for server tao / server tao is not allowed in this context.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `tao` / `kongor_game_client_manage` |
| **Expected** | Returns client status via TAO MCP. |
| **Actual (Symptom)** | Error: tool kongor_game_client_manage is not enabled for server tao / server tao is not allowed in this context. |
| **Root Cause** | Subagent context policy does not whitelist dynamic TAO MCP execution. |
| **Fix / Remediation** | Manage client lifecycle via PowerShell processes and scripts directly. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"action": "status"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server tao -Tool kongor_game_client_manage
```

## Prevention Invariant
Manage client lifecycle via PowerShell processes and scripts directly.
