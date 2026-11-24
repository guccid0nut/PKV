---
id: MCP-212
server: kongor
tool: kongor_vfs_resolve
category: parameter-mismatch
title: "KONGOR-VFS-RESOLVE-AUDIT-SHADOWS-OMITTED-PATTERN-ON-LONG-LIVED-STDIO-PROCESS"
status: investigating
codified_in: "src/tools/kongor-cli/Mcp/Handlers/VfsResolveHandler.cs"
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 5
date: 2026-10-14
last_seen: 2026-11-16
tags: [kongor, kongor_vfs_resolve, parameter-mismatch]
prevention_rule: "Always pass `'pattern': '*'` (or `'virtualPath': 'ui/scripts/main_v2.lua'`) explicitly when calling `kongor_vfs_resolve` against a long-lived stdio MCP process, and test updated C# MCP code directly via `kongor.exe mcp --transport http` or..."
---

# MCP-212: KONGOR-VFS-RESOLVE-AUDIT-SHADOWS-OMITTED-PATTERN-ON-LONG-LIVED-STDIO-PROCESS

## Summary
Tracked MCP issue on `kongor` / `kongor_vfs_resolve` (category: `parameter-mismatch`, status: `investigating`, occurrences: 5). Audit VFS shadow collisions across `override/` using default filter `*`. -> Returned `Missing required parameter 'virtualPath' (or 'pattern' for search).`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_resolve` |
| **Expected** | Audit VFS shadow collisions across `override/` using default filter `*`. |
| **Actual (Symptom)** | Returned `Missing required parameter 'virtualPath' (or 'pattern' for search).` |
| **Root Cause** | Although `VfsResolveHandler.cs` on disk handles `action: "audit_shadows"` with default `pattern ?? assetPath ?? "*"`, the long-lived IDE stdio MCP server process (`Temp\kongor_mcp_runtime\pid_29548\kongor.exe`, started 2026-10-13) was running an older build prior to the `audit_shadows` parameterless default. |
| **Fix / Remediation** | Always pass `"pattern": "*"` (or `"virtualPath": "ui/scripts/main_v2.lua"`) explicitly when calling `kongor_vfs_resolve` against a long-lived stdio MCP process, and test updated C# MCP code directly via `kongor.exe mcp --transport http` or rebuilt `Release` binary. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "audit_shadows"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_resolve
```

## Prevention Invariant
Always pass `'pattern': '*'` (or `'virtualPath': 'ui/scripts/main_v2.lua'`) explicitly when calling `kongor_vfs_resolve` against a long-lived stdio MCP process, and test updated C# MCP code directly via `kongor.exe mcp --transport http` or...
