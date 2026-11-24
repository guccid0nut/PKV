---
id: MCP-170
server: windbg
tool: open_cdb_dump
category: ipc-timeout
title: "(formerly MCP-107): WINDBG-OPEN-CDB-DUMP-3-MINUTE-TIMEOUT"
status: superseded
superseded_by: "MCP-129"
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [windbg, open_cdb_dump, ipc-timeout]
prevention_rule: "`mcp_windbg/server.py` now prioritizes local symbol cache (`_sanitize_offline_symbols`) and bounds dump open timeout to `25s` with `.exr -1; r; k 20` fallback."
---

# MCP-170: (formerly MCP-107): WINDBG-OPEN-CDB-DUMP-3-MINUTE-TIMEOUT

## Summary
Repeat occurrence of [`MCP-129`] on `windbg` / `open_cdb_dump` (`ipc-timeout`). Superseded by `MCP-129`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `open_cdb_dump` |
| **Expected** | Opens the crash minidump in CDB and returns a session ID. |
| **Actual (Symptom)** | Timed out after 3 minutes with `MCP tool call to server "windbg" timed out after 3m0s: context deadline exceeded`. |
| **Root Cause** | The CDB process spawned by the `windbg` MCP server blocked on symbol downloading/resolving (symsrv / Microsoft Symbol Server network connection timeout) or stdin prompt without returning control within the default MCP execution budget. |
| **Fix / Remediation** | `mcp_windbg/server.py` now prioritizes local symbol cache (`_sanitize_offline_symbols`) and bounds dump open timeout to `25s` with `.exr -1; r; k 20` fallback. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"dump_path": "games\\client\\logs\\crash\\minidump_20261010_132255.dmp"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool open_cdb_dump
```

## Prevention Invariant
`mcp_windbg/server.py` now prioritizes local symbol cache (`_sanitize_offline_symbols`) and bounds dump open timeout to `25s` with `.exr -1; r; k 20` fallback.
