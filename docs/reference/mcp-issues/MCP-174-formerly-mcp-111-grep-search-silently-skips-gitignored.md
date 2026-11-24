---
id: MCP-174
server: internal
tool: grep_search
category: runtime-error
title: "(formerly MCP-111): GREP-SEARCH-SILENTLY-SKIPS-GITIGNORED-TREES"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [internal, grep_search, runtime-error]
prevention_rule: "Use PowerShell Select-String directly across ClientPackageFiles, override, and game trees."
---

# MCP-174: (formerly MCP-111): GREP-SEARCH-SILENTLY-SKIPS-GITIGNORED-TREES

## Summary
Tracked MCP issue on `internal` / `grep_search` (category: `runtime-error`, status: `resolved`, occurrences: 1). Finds matching pattern in ClientPackageFiles and override folders. -> Silently returns 0 matches because ripgrep respects .gitignore rules by default.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `internal` / `grep_search` |
| **Expected** | Finds matching pattern in ClientPackageFiles and override folders. |
| **Actual (Symptom)** | Silently returns 0 matches because ripgrep respects .gitignore rules by default. |
| **Root Cause** | grep_search uses ripgrep without --no-ignore, skipping gitignored or LFS-tracked directories like ClientPackageFiles/ and override/. |
| **Fix / Remediation** | Use PowerShell Select-String directly across ClientPackageFiles, override, and game trees. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"SearchPath": "games\\client\\ClientPackageFiles", "Query": "store2"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server internal -Tool grep_search
```

## Prevention Invariant
Use PowerShell Select-String directly across ClientPackageFiles, override, and game trees.
