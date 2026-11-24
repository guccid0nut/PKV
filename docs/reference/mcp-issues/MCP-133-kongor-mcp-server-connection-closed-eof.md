---
id: MCP-133
server: kongor
tool: kongor_server_manage
category: transport-eof
title: "KONGOR-MCP-SERVER-CONNECTION-CLOSED-EOF"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-12
last_seen: 2026-10-12
tags: [kongor, kongor_server_manage, transport-eof]
prevention_rule: "Invoke via `python src/tools/tao_mcp.py --tool <tool> --args <args>` or restart stdio transport when pipe is closed."
---

# MCP-133: KONGOR-MCP-SERVER-CONNECTION-CLOSED-EOF

## Summary
Tracked MCP issue on `kongor` / `kongor_server_manage` (category: `transport-eof`, status: `resolved`, occurrences: 1). Aspire cluster services started via MCP server. -> `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_manage` |
| **Expected** | Aspire cluster services started via MCP server. |
| **Actual (Symptom)** | `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The stdio transport process for the lazy `kongor` MCP server was closed or terminated. |
| **Fix / Remediation** | For direct tool invocation when the stdio server is offline or closed, use `python src/tools/tao_mcp.py --tool <tool> --args <args>` or re-initialize the MCP server. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "start", "target": "cluster", "withGameServers": true}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_server_manage
```

## Prevention Invariant
Invoke via `python src/tools/tao_mcp.py --tool <tool> --args <args>` or restart stdio transport when pipe is closed.
