---
id: MCP-165
server: windbg
tool: open_cdb_dump
category: ipc-timeout
title: "(formerly MCP-102): WINDBG-OPEN-CDB-DUMP-TIMEOUT"
status: superseded
superseded_by: "MCP-129"
supersedes: []
occurrences: 1
date: 2026-10-08
last_seen: 2026-10-08
tags: [windbg, open_cdb_dump, ipc-timeout]
prevention_rule: "`mcp_windbg/server.py` now sanitizes default symbol paths to local cache (`C:\Symbols`) and bounds `CDB_DUMP_OPEN_TIMEOUT = 25` with automatic fast fallback to `.exr -1; r; k 20`."
---

# MCP-165: (formerly MCP-102): WINDBG-OPEN-CDB-DUMP-TIMEOUT

## Summary
Repeat occurrence of [`MCP-129`] on `windbg` / `open_cdb_dump` (`ipc-timeout`). Superseded by `MCP-129`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `open_cdb_dump` |
| **Expected** | Opens minidump session and returns analysis overview. |
| **Actual (Symptom)** | MCP tool call timed out after 3 minutes (context deadline exceeded). |
| **Root Cause** | `cdb.exe` or symbol server resolution stalled while attempting to fetch symbols or attach to dump without symbol cache pre-population. |
| **Fix / Remediation** | `mcp_windbg/server.py` now sanitizes default symbol paths to local cache (`C:\Symbols`) and bounds `CDB_DUMP_OPEN_TIMEOUT = 25` with automatic fast fallback to `.exr -1; r; k 20`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"dump_path": "games\\client\\logs\\crash\\minidump_20261008_000518.dmp"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool open_cdb_dump
```

## Prevention Invariant
`mcp_windbg/server.py` now sanitizes default symbol paths to local cache (`C:\Symbols`) and bounds `CDB_DUMP_OPEN_TIMEOUT = 25` with automatic fast fallback to `.exr -1; r; k 20`.
