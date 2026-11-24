---
id: MCP-144
server: kongor
tool: unknown
category: transport-eof
title: "(formerly MCP-082) [2026-09-29 08:40:19] Kongor MCP Server Stdio Connection EOF Disconnect"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-29
last_seen: 2026-09-29
tags: [kongor, transport-eof]
prevention_rule: "Fall back immediately to direct PowerShell process management (`Start-Process`, `Get-Process`, `Stop-Process`) and direct Willowmaker CLI invocations."
---

# MCP-144: (formerly MCP-082) [2026-09-29 08:40:19] Kongor MCP Server Stdio Connection EOF Disconnect

## Summary
Tracked MCP issue on `kongor` / `unknown` (category: `transport-eof`, status: `resolved`, occurrences: 1). - -> -

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `unknown` |
| **Expected** | - |
| **Actual (Symptom)** | - |
| **Root Cause** | The `kongor` MCP server child process closed its stdio transport stream on launch action, returning `connection closed: calling "tools/call": client is closing: EOF`. |
| **Fix / Remediation** | Fell back to direct PowerShell automation and WILLOWMAKER launcher. |

## Invocation Parameters & Raw Logs
```text
Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool unknown
```

## Prevention Invariant
Fall back immediately to direct PowerShell process management (`Start-Process`, `Get-Process`, `Stop-Process`) and direct Willowmaker CLI invocations.
