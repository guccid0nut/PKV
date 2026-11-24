---
id: MCP-172
server: windbg
tool: open_cdb_dump
category: ipc-timeout
title: "(formerly MCP-109): WINDBG-OPEN-CDB-DUMP-ZERO-BYTE-STALL"
status: superseded
superseded_by: "MCP-129"
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [windbg, open_cdb_dump, ipc-timeout]
prevention_rule: "`CDBSession.__init__` in `mcp_windbg/cdb_session.py` now validates file size `>= 64` bytes and the 4-byte `MDMP` magic header before spawning `cdb.exe`."
---

# MCP-172: (formerly MCP-109): WINDBG-OPEN-CDB-DUMP-ZERO-BYTE-STALL

## Summary
Repeat occurrence of [`MCP-129`] on `windbg` / `open_cdb_dump` (`ipc-timeout`). Superseded by `MCP-129`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `open_cdb_dump` |
| **Expected** | Rejects 0-byte corrupt dump immediately. |
| **Actual (Symptom)** | Timed out after 3 minutes because cdb hung indefinitely waiting for valid minidump headers. |
| **Root Cause** | CDB cannot open 0-byte dump files and hangs waiting for stream input instead of returning failure. |
| **Fix / Remediation** | `CDBSession.__init__` in `mcp_windbg/cdb_session.py` now validates file size `>= 64` bytes and the 4-byte `MDMP` magic header before spawning `cdb.exe`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"dump_path": "games\\client\\logs\\crash\\minidump_20261010_132255.dmp"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool open_cdb_dump
```

## Prevention Invariant
`CDBSession.__init__` in `mcp_windbg/cdb_session.py` now validates file size `>= 64` bytes and the 4-byte `MDMP` magic header before spawning `cdb.exe`.
