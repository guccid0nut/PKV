---
id: MCP-190
server: git
tool: git_status
category: window-resolution
title: "GIT-STATUS-CONTAINER-PATH-MISMATCH (MCP-2026-10-06)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [git, git_status, window-resolution]
prevention_rule: "Use `repo_path: '/workspace'` when interacting with containerized Git MCP, or run `git status` directly in PowerShell."
---

# MCP-190: GIT-STATUS-CONTAINER-PATH-MISMATCH (MCP-2026-10-06)

## Summary
Tracked MCP issue on `git` / `git_status` (category: `window-resolution`, status: `resolved`, occurrences: 1). `git_status` returns status for host path. -> `Repository path '<host_path>' is outside the allowed repository '/workspace'`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `git` / `git_status` |
| **Expected** | `git_status` returns status for host path. |
| **Actual (Symptom)** | `Repository path '<host_path>' is outside the allowed repository '/workspace'`. |
| **Root Cause** | The `git` MCP server container is mounted with `/workspace` as its allowed repository root. Host Windows paths are rejected. |
| **Fix / Remediation** | Use `repo_path: "/workspace"` when interacting with containerized Git MCP, or run `git status` directly in PowerShell. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"repo_path": "<host_path>"}`
* **Expected**: `git_status` returns status for host path.
* **Actual**: `Repository path '<host_path>' is outside the allowed repository '/workspace'`.
* **Root Cause**: The `git` MCP server container is mounted with `/workspace` as its allowed repository root. Host Windows paths are rejected.
* **Status**: `[RESOLVED]` (Identified containerized Git path constraint).
* **Next-Time Prevention Rule**: Use `repo_path: "/workspace"` when interacting with containerized Git MCP, or run `git status` directly in PowerShell.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server git -Tool git_status
```

## Prevention Invariant
Use `repo_path: '/workspace'` when interacting with containerized Git MCP, or run `git status` directly in PowerShell.
