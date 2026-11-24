---
id: MCP-171
server: everything
tool: everything_search
category: runtime-error
title: "(formerly MCP-108): EVERYTHING-SEARCH-OR-QUERY-WITH-PATH-RETURNS-ZERO"
status: superseded
superseded_by: "MCP-120"
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [everything, everything_search, runtime-error]
prevention_rule: "`src/tools/everything/mcp_server.py` now automatically splits compound `/` queries into sub-queries when `path` is specified, scoping each branch to `path` and merging/sorting the union."
---

# MCP-171: (formerly MCP-108): EVERYTHING-SEARCH-OR-QUERY-WITH-PATH-RETURNS-ZERO

## Summary
Repeat occurrence of [`MCP-120`] on `everything` / `everything_search` (`runtime-error`). Superseded by `MCP-120`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `everything` / `everything_search` |
| **Expected** | Returns matches for either log or dmp files under the given path. |
| **Actual (Symptom)** | Returns 0 results because Everything search query parser treats compound pipe expressions alongside path filter incorrectly. |
| **Root Cause** | Everything SDK/IPC query syntax parses pipe / with lower precedence than space-delimited path prefixes without explicit grouping. |
| **Fix / Remediation** | `src/tools/everything/mcp_server.py` now automatically splits compound `/` queries into sub-queries when `path` is specified, scoping each branch to `path` and merging/sorting the union. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"query": "ext:log | ext:dmp", "path": "<workspace>"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server everything -Tool everything_search
```

## Prevention Invariant
`src/tools/everything/mcp_server.py` now automatically splits compound `/` queries into sub-queries when `path` is specified, scoping each branch to `path` and merging/sorting the union.
