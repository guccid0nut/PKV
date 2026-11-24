---
id: MCP-131
server: windbg
tool: open_cdb_dump
category: ipc-timeout
title: "WINDBG-OPEN-CDB-DUMP-TIMEOUT-ONLINE-SYMBOLS"
status: superseded
superseded_by: "MCP-129"
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [windbg, open_cdb_dump, ipc-timeout]
prevention_rule: "Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` prioritizes local `C:\Symbols` cache and falls back to `.exr -1; r; k 20` if `!analyze -v` exceeds 25 seconds."
---

# MCP-131: WINDBG-OPEN-CDB-DUMP-TIMEOUT-ONLINE-SYMBOLS

## Summary
Repeat occurrence of [`MCP-129`] on `windbg` / `open_cdb_dump` (`ipc-timeout`). Superseded by `MCP-129`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `open_cdb_dump` |
| **Expected** | CDB dump opened and initial stack analysis returned. |
| **Actual (Symptom)** | Tool call timed out after 3m0s with context deadline exceeded. |
| **Root Cause** | `open_cdb_dump` stalled on Microsoft online symbol resolution for external DLLs while holding the tool lock. |
| **Fix / Remediation** | Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` prioritizes local `C:\Symbols` cache and falls back to `.exr -1; r; k 20` if `!analyze -v` exceeds 25 seconds. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"dump_path": "games\\client\\logs\\crash\\minidump_20261011_233203.dmp"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool open_cdb_dump
```

## Prevention Invariant
Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` prioritizes local `C:\Symbols` cache and falls back to `.exr -1; r; k 20` if `!analyze -v` exceeds 25 seconds.
