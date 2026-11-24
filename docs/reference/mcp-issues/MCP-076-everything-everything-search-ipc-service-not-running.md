---
id: MCP-076
server: everything
tool: everything_search
category: client-offline
title: "`everything` everything_search IPC Service Not Running"
status: documented
superseded_by: ""
supersedes: ["MCP-083"]
occurrences: 2
date: 2026-09-25
last_seen: 2026-09-25
tags: [everything, everything_search, client-offline]
prevention_rule: "Verify `Everything.exe` is running or fall back to PowerShell `Get-ChildItem`."
---

# MCP-076: `everything` everything_search IPC Service Not Running

## Summary
Tracked MCP issue on `everything` / `everything_search` (category: `client-offline`, status: `resolved`, occurrences: 2). List of matching file paths indexing `console.log`. -> Returned `{ "success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running." }`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | List of matching file paths indexing `console.log`. |
| **Actual (Symptom)** | Returned `{ "success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running." }`. |
| **Root Cause** | Voidtools Everything search service/application is not running in the current desktop session, preventing IPC access to the indexer. |
| **Fix / Remediation** | Fall back to PowerShell directory querying (`Get-ChildItem`) and direct path inspection. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "console.log"
  }
  

Logs:

  {
    "success": false,
    "error": "Error 8: Everything IPC not found. Please make sure Everything is running.",
    "query": "console.log",
    "count": 0,
    "results": []
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Verify `Everything.exe` is running or fall back to PowerShell `Get-ChildItem`.
