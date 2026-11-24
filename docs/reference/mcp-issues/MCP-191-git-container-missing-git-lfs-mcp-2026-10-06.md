---
id: MCP-191
server: git
tool: git_status
category: binary-decode
title: "GIT-CONTAINER-MISSING-GIT-LFS (MCP-2026-10-06)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [git, git_status, binary-decode]
prevention_rule: "Verify git / git_status readiness and parameters before invocation."
---

# MCP-191: GIT-CONTAINER-MISSING-GIT-LFS (MCP-2026-10-06)

## Summary
Tracked MCP issue on `git` / `git_status` (category: `binary-decode`, status: `resolved`, occurrences: 1). `git_status` returns repository status. -> `Cmd('git') failed due to: exit code(128) stderr: 'git-lfs filter-process: 1: git-lfs: not found fatal: the remote end hung up unexpectedly'`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `git` / `git_status` |
| **Expected** | `git_status` returns repository status. |
| **Actual (Symptom)** | `Cmd('git') failed due to: exit code(128) stderr: 'git-lfs filter-process: 1: git-lfs: not found fatal: the remote end hung up unexpectedly'`. |
| **Root Cause** | The Docker container running `mcp/git` does not have `git-lfs` installed, whereas Project Kongor repository requires git-lfs filters for binary assets. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"repo_path": "/workspace"}`
* **Expected**: `git_status` returns repository status.
* **Actual**: `Cmd('git') failed due to: exit code(128) stderr: 'git-lfs filter-process: 1: git-lfs: not found fatal: the remote end hung up unexpectedly'`.
* **Root Cause**: The Docker container running `mcp/git` does not have `git-lfs` installed, whereas Project Kongor repository requires git-lfs filters for binary assets.
* **Status**: `[RESOLVED]` (For repositories using Git LFS, invoke git via native host PowerShell `git status` / `git diff` rather than containerized git MCP).
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server git -Tool git_status
```

## Prevention Invariant
Verify git / git_status readiness and parameters before invocation.
