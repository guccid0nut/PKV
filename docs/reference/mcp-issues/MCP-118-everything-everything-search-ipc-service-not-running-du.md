---
id: MCP-118
server: everything
tool: everything_search
category: window-resolution
title: "`everything` / `everything_search` IPC Service Not Running During Game Search"
status: superseded
superseded_by: "MCP-091"
supersedes: []
occurrences: 1
date: 2026-09-27
last_seen: 2026-09-27
tags: [everything, everything_search, window-resolution]
prevention_rule: "Before invoking `everything_search`, verify that `Everything.exe` is running via `Get-Process Everything -ErrorAction SilentlyContinue`. If offline, fall back directly to PowerShell filesystem inspection across drives."
---

# MCP-118: `everything` / `everything_search` IPC Service Not Running During Game Search

## Summary
Repeat occurrence of [`MCP-091`] on `everything` / `everything_search` (`window-resolution`). Superseded by `MCP-091`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Rapid list of older game installation directories containing `hon_x64.exe` on local drives. |
| **Actual (Symptom)** | Returned error: `{"success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running.", "count": 0, "results": []}`. |
| **Root Cause** | The Voidtools Everything desktop service/application (`Everything.exe`) is not currently active in the Windows session, so the named IPC window message hook cannot be contacted. |
| **Fix / Remediation** | Gracefully fell back to PowerShell `Get-ChildItem` across drives to locate older game installations. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "hon_x64.exe"
  }
  

Logs:

  {
    "success": false,
    "error": "Error 8: Everything IPC not found. Please make sure Everything is running.",
    "query": "hon_x64.exe",
    "count": 0,
    "results": []
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Before invoking `everything_search`, verify that `Everything.exe` is running via `Get-Process Everything -ErrorAction SilentlyContinue`. If offline, fall back directly to PowerShell filesystem inspection across drives.
