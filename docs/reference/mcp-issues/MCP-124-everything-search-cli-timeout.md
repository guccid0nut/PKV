---
id: MCP-124
server: everything
tool: everything_search
category: ipc-timeout
title: "EVERYTHING-SEARCH-CLI-TIMEOUT"
status: codified
codified_in: "src/tools/everything/mcp_server.py"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-11
last_seen: 2026-11-11
tags: [everything, everything_search, ipc-timeout]
prevention_rule: "Patched in `src/tools/everything/mcp_server.py`: on `es.exe` IPC failure or timeout, `_python_fallback_search` executes a bounded native directory scan within `path` or `REPO_ROOT`."
---

# MCP-124: EVERYTHING-SEARCH-CLI-TIMEOUT

## Summary
Tracked MCP issue on `everything` / `everything_search` (category: `ipc-timeout`, status: `codified`, occurrences: 4). List of matching files within 10s. -> `es.exe` timed out after 10.0 seconds.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | List of matching files within 10s. |
| **Actual (Symptom)** | `es.exe` timed out after 10.0 seconds. |
| **Root Cause** | `es.exe` query with wildcard prefix `*settings*.json` and sort across entire drive path timed out waiting for IPC response from background Everything service. |
| **Fix / Remediation** | Patched in `src/tools/everything/mcp_server.py`: on `es.exe` IPC failure or timeout, `_python_fallback_search` executes a bounded native directory scan within `path` or `REPO_ROOT`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"path": "<workspace>", "query": "*settings*.json", "sort": "date-modified-desc"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
Patched in `src/tools/everything/mcp_server.py`: on `es.exe` IPC failure or timeout, `_python_fallback_search` executes a bounded native directory scan within `path` or `REPO_ROOT`.
