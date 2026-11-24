---
id: MCP-185
server: kongor
tool: kongor_willowmaker_manage
category: transport-eof
title: "WILLOWMAKER-MANAGE-EOF (MCP-2026-10-06)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-06
last_seen: 2026-11-04
tags: [kongor, kongor_willowmaker_manage, transport-eof]
prevention_rule: "Check and launch WILLOWMAKER directly via PowerShell or dotnet run when `kongor` MCP server stdio transport is closed."
---

# MCP-185: WILLOWMAKER-MANAGE-EOF (MCP-2026-10-06)

## Summary
Tracked MCP issue on `kongor` / `kongor_willowmaker_manage` (category: `transport-eof`, status: `documented`, occurrences: 2). Return WILLOWMAKER launcher status. -> Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_willowmaker_manage` |
| **Expected** | Return WILLOWMAKER launcher status. |
| **Actual (Symptom)** | Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The stdio transport for the `kongor` MCP server was closed / not restarted. |
| **Fix / Remediation** | Check and launch WILLOWMAKER directly via PowerShell or dotnet run when `kongor` MCP server stdio transport is closed. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action":"status"}`
* **Expected**: Return WILLOWMAKER launcher status.
* **Actual**: Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.
* **Root Cause**: The stdio transport for the `kongor` MCP server was closed / not restarted.
* **Status**: `[RESOLVED]` (Managing WILLOWMAKER via direct PowerShell process management `Start-Process`, `Get-Process`).
* **Next-Time Prevention Rule**: Check and launch WILLOWMAKER directly via PowerShell or dotnet run when `kongor` MCP server stdio transport is closed.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_willowmaker_manage
```

## Prevention Invariant
Check and launch WILLOWMAKER directly via PowerShell or dotnet run when `kongor` MCP server stdio transport is closed.
