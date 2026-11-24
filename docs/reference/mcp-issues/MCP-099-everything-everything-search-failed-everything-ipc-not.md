---
id: MCP-099
server: everything
tool: everything_search
category: window-resolution
title: "`everything` / `everything_search` Failed: Everything IPC Not Found"
status: superseded
superseded_by: "MCP-091"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [everything, everything_search, window-resolution]
prevention_rule: "When Everything IPC is not available, fall back directly to PowerShell `Get-ChildItem` search."
---

# MCP-099: `everything` / `everything_search` Failed: Everything IPC Not Found

## Summary
Repeat occurrence of [`MCP-091`] on `everything` / `everything_search` (`window-resolution`). Superseded by `MCP-091`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Search for files named `console.log` on the filesystem via Everything search indexing. |
| **Actual (Symptom)** | Returned `{"success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running."}` |
| **Root Cause** | Voidtools Everything desktop client is not running in the background, so the IPC window / named pipe is not registered. |
| **Fix / Remediation** | Switched to PowerShell `Get-ChildItem` for filesystem searching. |

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
When Everything IPC is not available, fall back directly to PowerShell `Get-ChildItem` search.
