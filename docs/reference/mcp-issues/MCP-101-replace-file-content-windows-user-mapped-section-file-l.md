---
id: MCP-101
server: internal
tool: replace_file_content
category: window-resolution
title: "`replace_file_content` Windows User-Mapped Section File Locking"
status: superseded
superseded_by: "MCP-059"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [internal, replace_file_content, window-resolution]
prevention_rule: "Await background search/grep task completion or verify file handle release with `[System.IO.File]::OpenWrite(...)` before editing."
---

# MCP-101: `replace_file_content` Windows User-Mapped Section File Locking

## Summary
Repeat occurrence of [`MCP-059`] on `internal` / `replace_file_content` (`window-resolution`). Superseded by `MCP-059`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `internal` / `replace_file_content` |
| **Expected** | In-place replacement of content in `Index.cshtml`. |
| **Actual (Symptom)** | Failed with `The requested operation cannot be performed on a file with a user-mapped section open`. |
| **Root Cause** | A background search task held a memory-mapped section on the file while searching for strings. |
| **Fix / Remediation** | Re-verified write handle and successfully executed `replace_file_content` after background search process closed. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "TargetFile": "src/services/web-ui/Views/ArMiniGames/Index.cshtml"
  }
  

Logs:

  open c:/ProjectKongor/src/services/web-ui/Views/ArMiniGames/Index.cshtml: The requested operation cannot be performed on a file with a user-mapped section open.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server internal -Tool replace_file_content
```

## Prevention Invariant
Await background search/grep task completion or verify file handle release with `[System.IO.File]::OpenWrite(...)` before editing.
