---
id: MCP-009
server: everything
tool: everything_search
category: window-resolution
title: "Everything MCP IPC Not Found (Everything.exe Offline)"
status: superseded
superseded_by: "MCP-091"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [everything, everything_search, window-resolution]
prevention_rule: "Check if Everything.exe is running; fallback to `grep_search` or PowerShell `Get-ChildItem`."
---

# MCP-009: Everything MCP IPC Not Found (Everything.exe Offline)

## Summary
Repeat occurrence of [`MCP-091`] on `everything` / `everything_search` (`window-resolution`). Superseded by `MCP-091`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Rapid list of matching files from Voidtools Everything indexing. |
| **Actual (Symptom)** | Returned error: `{"success": false, "error": "Error 8: Everything IPC not found. Please make sure Everything is running.", "count": 0, "results": []}`. |
| **Root Cause** | The Voidtools Everything desktop service/application (`Everything.exe`) is not currently active in the Windows session, so the named IPC window message hook cannot be contacted. |
| **Fix / Remediation** | Automatically fallback to `grep_search` / `find_by_name` / PowerShell `Get-ChildItem`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "path": "C:\\ProjectKongor",
    "query": "ZstdDeltaCompressor"
  }
  

Logs:

  {
    "success": false,
    "error": "Error 8: Everything IPC not found. Please make sure Everything is running.",
    "query": "ZstdDeltaCompressor",
    "count": 0,
    "results": []
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Check if Everything.exe is running; fallback to `grep_search` or PowerShell `Get-ChildItem`.
