---
id: MCP-117
server: everything
tool: everything_search
category: parameter-mismatch
title: "`everything` / `everything_search` Unknown Sort Parameter & IPC Offline Error"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-27
last_seen: 2026-09-27
tags: [everything, everything_search, parameter-mismatch]
prevention_rule: "Check whether `Everything.exe` is running before calling `everything_search`. Use `sort: 'size'` when querying by size. If `Everything.exe` is offline, fall back to PowerShell filesystem inspection."
---

# MCP-117: `everything` / `everything_search` Unknown Sort Parameter & IPC Offline Error

## Summary
Tracked MCP issue on `everything` / `everything_search` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Retrieve list of files larger than 5GB sorted by size descending. -> Failed with `Error 4: Unknown sort: size-desc`, and subsequent call with `sort: "size"` returned `Error 8: Everything IPC not found. Please make sure Everything is running.`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Retrieve list of files larger than 5GB sorted by size descending. |
| **Actual (Symptom)** | Failed with `Error 4: Unknown sort: size-desc`, and subsequent call with `sort: "size"` returned `Error 8: Everything IPC not found. Please make sure Everything is running.`. |
| **Root Cause** | `everything_search` schema defines valid sort values as `name`, `path`, `size`, `date-modified-desc`, `date-created-desc` (not `size-desc`). Additionally, the Everything background process (`Everything.exe`) is not currently active on the host, preventing IPC communication. |
| **Fix / Remediation** | Check whether `Everything.exe` is running before calling `everything_search`. Use `sort: "size"` when querying by size. If `Everything.exe` is offline, fall back to PowerShell filesystem inspection. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "size:>5gb",
    "sort": "size-desc",
    "max_results": 20
  }
  

Logs:

  Error 4: Unknown sort: size-desc
  Error 8: Everything IPC not found. Please make sure Everything is running.
  ---
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Check whether `Everything.exe` is running before calling `everything_search`. Use `sort: 'size'` when querying by size. If `Everything.exe` is offline, fall back to PowerShell filesystem inspection.
