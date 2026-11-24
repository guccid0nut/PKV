---
id: MCP-200
server: kongor
tool: kongor_vfs_locate
category: parameter-mismatch
title: "KONGOR-VFS-LOCATE-PARAMETER-NAME-QUERY-VS-PATTERN"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-13
last_seen: 2026-11-16
tags: [kongor, kongor_vfs_locate, parameter-mismatch]
prevention_rule: "Always pass `virtual_path` or `query` when calling `kongor_vfs_locate`."
---

# MCP-200: KONGOR-VFS-LOCATE-PARAMETER-NAME-QUERY-VS-PATTERN

## Summary
Tracked MCP issue on `kongor` / `kongor_vfs_locate` (category: `parameter-mismatch`, status: `documented`, occurrences: 2). Locate Pearl Alt3 VFS entries. -> Returned `Missing required parameter 'virtual_path' (or 'query' / 'filename')`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_locate` |
| **Expected** | Locate Pearl Alt3 VFS entries. |
| **Actual (Symptom)** | Returned `Missing required parameter 'virtual_path' (or 'query' / 'filename')`. |
| **Root Cause** | `kongor_vfs_locate` expects `virtual_path`, `query`, or `filename` rather than `pattern`. |
| **Fix / Remediation** | Always pass `virtual_path` or `query` when calling `kongor_vfs_locate`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"pattern": "*pearl*alt3*"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_locate
```

## Prevention Invariant
Always pass `virtual_path` or `query` when calling `kongor_vfs_locate`.
