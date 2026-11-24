---
id: MCP-060
server: kongor
tool: kongor_game_command
category: lua-sandbox
title: "Missing Required Parameter 'command' in kongor_game_command Invocations & Script Pre-Remediation"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-09-25
last_seen: 2026-10-20
tags: [kongor, kongor_game_command, lua-sandbox]
prevention_rule: "`GameCommandHandler` routes `action: 'status'` gracefully; `GameClientManageHandler` pre-remediates `Script` commands and blocks sandbox violations."
---

# MCP-060: Missing Required Parameter 'command' in kongor_game_command Invocations & Script Pre-Remediation

## Summary
Tracked MCP issue on `kongor` / `kongor_game_command` (category: `lua-sandbox`, status: `investigating`, occurrences: 2). Retrieve game client status or console health. -> Failed with schema validation error: `Missing required parameter 'command'.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Retrieve game client status or console health. |
| **Actual (Symptom)** | Failed with schema validation error: `Missing required parameter 'command'.` |
| **Root Cause** | The `kongor_game_command` tool previously required a `command: string` argument regardless of whether `action: "status"` was provided. Furthermore, when `command` contained a `Script ...` Lua payload, `ExecuteClientCommandAsync` dispatched it directly without running pre-execution self-healing, causing crashes on `LuaTrigger.GetTrigger` and sandbox... |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameCommandHandler.cs`: Gracefully handles `action: "status"`, `"state"`, or `"inspect"` when `command` is omitted, automatically routing to `GameClientManageHandler.HandleAsync` (or `GameServerManageHandler.HandleAsync` for dedicated servers). - Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameC... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "status"
  }
  

Logs:

  Encountered error in tool execution: Missing required parameter 'command'.
  client_command_errors.ndjson: attempt to index global 'LuaTrigger' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
`GameCommandHandler` routes `action: 'status'` gracefully; `GameClientManageHandler` pre-remediates `Script` commands and blocks sandbox violations.
