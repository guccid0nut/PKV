---
id: MCP-173
server: internal
tool: es.exe
category: lua-sandbox
title: "(formerly MCP-110): EVERYTHING-ES-CLI-IPC-UNAVAILABLE-IN-SANDBOX"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [internal, es.exe, lua-sandbox]
prevention_rule: "`src/tools/everything/mcp_server.py` automatically falls back to a native Python filesystem scanner (`_python_fallback_search`) whenever `es.exe` IPC is unreachable."
---

# MCP-173: (formerly MCP-110): EVERYTHING-ES-CLI-IPC-UNAVAILABLE-IN-SANDBOX

## Summary
Tracked MCP issue on `internal` / `es.exe` (category: `lua-sandbox`, status: `patched`, occurrences: 1). Returns indexed file paths via Everything CLI. -> Returns error: Everything IPC window not found.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `internal` / `es.exe` |
| **Expected** | Returns indexed file paths via Everything CLI. |
| **Actual (Symptom)** | Returns error: Everything IPC window not found. |
| **Root Cause** | Windows terminal sandbox blocks window message IPC (WM_COPYDATA) used by es.exe to communicate with the GUI Everything service. |
| **Fix / Remediation** | `src/tools/everything/mcp_server.py` automatically falls back to a native Python filesystem scanner (`_python_fallback_search`) whenever `es.exe` IPC is unreachable. |

## Invocation Parameters & Raw Logs
```text
Parameters:
es.exe -path <workspace> *.log
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server internal -Tool es.exe
```

## Prevention Invariant
`src/tools/everything/mcp_server.py` automatically falls back to a native Python filesystem scanner (`_python_fallback_search`) whenever `es.exe` IPC is unreachable.
