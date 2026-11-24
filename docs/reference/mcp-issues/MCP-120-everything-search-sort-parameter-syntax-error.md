---
id: MCP-120
server: everything
tool: everything_search
category: runtime-error
title: "EVERYTHING-SEARCH-SORT-PARAMETER-SYNTAX-ERROR"
status: codified
codified_in: "src/tools/everything/mcp_server.py"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-171"]
occurrences: 4
date: 2026-10-11
last_seen: 2026-10-19
tags: [everything, everything_search, runtime-error]
prevention_rule: "Patched in `src/tools/everything/mcp_server.py`: `_normalize_sort` automatically normalizes underscores, aliases, and `-desc`/`-asc` suffixes before invoking `es.exe`."
---

# MCP-120: EVERYTHING-SEARCH-SORT-PARAMETER-SYNTAX-ERROR

## Summary
Tracked MCP issue on `everything` / `everything_search` (category: `runtime-error`, status: `patched`, occurrences: 2). Returns files sorted by date modified descending. -> Returns `Error 4: Unknown sort: date_modified_desc`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Returns files sorted by date modified descending. |
| **Actual (Symptom)** | Returns `Error 4: Unknown sort: date_modified_desc`. |
| **Root Cause** | `everything_search` requires hyphens in sort enum: `"date-modified-desc"`, not underscores (`"date_modified_desc"`). |
| **Fix / Remediation** | Patched in `src/tools/everything/mcp_server.py`: `_normalize_sort` automatically normalizes underscores, aliases, and `-desc`/`-asc` suffixes before invoking `es.exe`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"path": "<workspace>", "query": "ext:log|ext:dmp", "sort": "date_modified_desc"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Patched in `src/tools/everything/mcp_server.py`: `_normalize_sort` automatically normalizes underscores, aliases, and `-desc`/`-asc` suffixes before invoking `es.exe`.
