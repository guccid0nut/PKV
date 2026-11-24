---
id: MCP-091
server: everything
tool: everything_search
category: window-resolution
title: "Everything MCP Tool Failed: Everything IPC Not Found (Everything.exe Offline)"
status: prevented
superseded_by: ""
supersedes: ["MCP-009", "MCP-099", "MCP-118"]
occurrences: 4
date: 2026-09-26
last_seen: 2026-09-27
tags: [everything, everything_search, window-resolution]
prevention_rule: "Always check if `Everything.exe` is active before querying `everything_search`. If offline, use PowerShell `Get-ChildItem -Filter` or `Select-String` directly."
---

# MCP-091: Everything MCP Tool Failed: Everything IPC Not Found (Everything.exe Offline)

## Summary
Tracked MCP issue on `everything` / `everything_search` (category: `window-resolution`, status: `prevented`, occurrences: 4). Rapid file location lookup for `console.log` across the Windows file system. -> Failed with `{"success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running.", "count": 0, "results": []}`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Rapid file location lookup for `console.log` across the Windows file system. |
| **Actual (Symptom)** | Failed with `{"success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running.", "count": 0, "results": []}`. |
| **Root Cause** | `Everything.exe` is not running in the current user session, so the named IPC window message hook cannot be reached. |
| **Fix / Remediation** | Always check if `Everything.exe` is active before querying `everything_search`. If offline, use PowerShell `Get-ChildItem -Filter` or `Select-String` directly. |

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
Always check if `Everything.exe` is active before querying `everything_search`. If offline, use PowerShell `Get-ChildItem -Filter` or `Select-String` directly.
