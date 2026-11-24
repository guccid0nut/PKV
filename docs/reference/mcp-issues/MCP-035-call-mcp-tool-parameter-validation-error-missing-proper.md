---
id: MCP-035
server: system
tool: call_mcp_tool
category: parameter-mismatch
title: "`call_mcp_tool` Parameter Validation Error: Missing Property `ToolName`"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [system, call_mcp_tool, parameter-mismatch]
prevention_rule: "Always pass `ToolName`, `ServerName`, and `Arguments` explicitly to `call_mcp_tool`."
---

# MCP-035: `call_mcp_tool` Parameter Validation Error: Missing Property `ToolName`

## Summary
Tracked MCP issue on `system` / `call_mcp_tool` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Retrieve status of player game client. -> Returned tool validation error: `Encountered error in tool validation: invalid arguments: - missing property 'ToolName'`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `system` / `call_mcp_tool` |
| **Expected** | Retrieve status of player game client. |
| **Actual (Symptom)** | Returned tool validation error: `Encountered error in tool validation: invalid arguments: - missing property 'ToolName'`. |
| **Root Cause** | The lazy-loaded MCP invocation tool `call_mcp_tool` requires `ToolName`, `ServerName`, and `Arguments` as mandatory parameters. `ToolName` was omitted during the call. |
| **Fix / Remediation** | Enforced presence of `ToolName` in all `call_mcp_tool` invocations. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "Arguments": { "action": "status" },
    "ServerName": "kongor"
  }
  

Logs:

  Encountered error in tool validation: invalid arguments:
  - missing property 'ToolName'
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server system -Tool call_mcp_tool
```

## Prevention Invariant
Always pass `ToolName`, `ServerName`, and `Arguments` explicitly to `call_mcp_tool`.
