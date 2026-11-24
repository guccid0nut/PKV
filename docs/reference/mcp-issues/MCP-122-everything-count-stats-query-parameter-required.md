---
id: MCP-122
server: everything
tool: everything_count_stats
category: parameter-mismatch
title: "EVERYTHING-COUNT-STATS-QUERY-PARAMETER-REQUIRED"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [everything, everything_count_stats, parameter-mismatch]
prevention_rule: "Patched in `src/tools/everything/mcp_server.py`: `everything_count_stats` now defaults `query: str = '*'`."
---

# MCP-122: EVERYTHING-COUNT-STATS-QUERY-PARAMETER-REQUIRED

## Summary
Tracked MCP issue on `everything` / `everything_count_stats` (category: `parameter-mismatch`, status: `patched`, occurrences: 1). Count of all files under path without explicit query. -> Validation error: `query` Field required.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_count_stats` |
| **Expected** | Count of all files under path without explicit query. |
| **Actual (Symptom)** | Validation error: `query` Field required. |
| **Root Cause** | `everything_count_stats` requires `query` argument (e.g. `*` or a search term). |
| **Fix / Remediation** | Patched in `src/tools/everything/mcp_server.py`: `everything_count_stats` now defaults `query: str = "*"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"path": "games\\client\\game"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_count_stats
```

## Prevention Invariant
Patched in `src/tools/everything/mcp_server.py`: `everything_count_stats` now defaults `query: str = '*'`.
