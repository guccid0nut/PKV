---
id: MCP-278
server: kongor
tool: kongor_call_tool
category: invalid-example
title: "Describe emits failing VFS resolve example without virtualPath"
status: resolved
verified_by: "3379aa36e + McpToolContractTests.Tranche3 + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_call_tool, invalid-example]
prevention_rule: "Generate action-aware example arguments and test examples against the target handler; resolve requires a virtualPath."
---

# MCP-278: Describe emits failing VFS resolve example without virtualPath

## Summary
`kongor_call_tool` describe generates an example bridge call for `kongor_vfs_resolve` that lacks the path required by its default resolve action. Executing that example fails on the first attempt. Open; no application fix has been applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_call_tool` |
| **Expected** | The example bridge call supplied by describe contains valid arguments for the chosen action. |
| **Actual (Symptom)** | The generated call with `arguments_json` containing only `action: resolve` fails with missing `virtualPath`. |
| **Root Cause** | `CallToolHandler.BuildToolDescribeResult` generates arguments from the schema's unconditional required fields, or falls back to the action default, without accounting for action-dependent requirements. |
| **Fix / Remediation** | Not fixed. Supply `virtualPath` explicitly for resolve; consider action-aware examples and execution tests. The automatically attached `MCP-212` concerns a stale process and a different action, not this root cause. |

## Invocation Parameters & Raw Logs
```text
Describe: action=describe, tool_name=kongor_vfs_resolve
Generated bridge call: action=call, tool_name=kongor_vfs_resolve, arguments_json={action: resolve}
Error: Missing required parameter 'virtualPath' (or 'pattern' for search, or action='audit_shadows'). Fix: Pass a virtualPath or use action='audit_shadows'.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_call_tool
```

## Prevention Invariant
Generate action-aware example arguments and test examples against the target handler; resolve requires a virtualPath.
