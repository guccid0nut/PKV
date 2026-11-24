---
id: MCP-083
server: everything
tool: everything_search
category: client-offline
title: "`everything` / `everything_search` IPC Service Not Available"
status: superseded
superseded_by: "MCP-076"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [everything, everything_search, client-offline]
prevention_rule: "Do not invoke `everything` without first confirming `Everything.exe` is running; use targeted PowerShell `Get-ChildItem` instead."
---

# MCP-083: `everything` / `everything_search` IPC Service Not Available

## Summary
Repeat occurrence of [`MCP-076`] on `everything` / `everything_search` (`client-offline`). Superseded by `MCP-076`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Rapid file location across filesystem for `console.log`. |
| **Actual (Symptom)** | Failed with `Error 8: Everything IPC not found. Please make sure Everything is running.`. |
| **Root Cause** | Voidtools Everything search service/IPC engine is not active or running in the background. |
| **Fix / Remediation** | Do not invoke `everything` without first confirming `Everything.exe` is running; use targeted PowerShell `Get-ChildItem` instead. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "console.log"
  }
  

Logs:

  Error 8: Everything IPC not found. Please make sure Everything is running.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Do not invoke `everything` without first confirming `Everything.exe` is running; use targeted PowerShell `Get-ChildItem` instead.
