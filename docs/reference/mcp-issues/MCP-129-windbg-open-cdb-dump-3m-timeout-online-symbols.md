---
id: MCP-129
server: windbg
tool: open_cdb_dump
category: ipc-timeout
title: "WINDBG-OPEN-CDB-DUMP-3M-TIMEOUT-ONLINE-SYMBOLS"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-165", "MCP-170", "MCP-172", "MCP-131"]
occurrences: 6
date: 2026-10-11
last_seen: 2026-10-19
tags: [windbg, open_cdb_dump, ipc-timeout]
prevention_rule: "Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` enforces offline-first local `C:\Symbols` resolution and `CDB_DUMP_OPEN_TIMEOUT = 25` prevents 3-minute hangs."
---

# MCP-129: WINDBG-OPEN-CDB-DUMP-3M-TIMEOUT-ONLINE-SYMBOLS

## Summary
Tracked MCP issue on `windbg` / `open_cdb_dump` (category: `ipc-timeout`, status: `patched`, occurrences: 5). CDB triage output with stack trace and crash analysis. -> MCP tool call timed out after 3m0s: context deadline exceeded.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `open_cdb_dump` |
| **Expected** | CDB triage output with stack trace and crash analysis. |
| **Actual (Symptom)** | MCP tool call timed out after 3m0s: context deadline exceeded. |
| **Root Cause** | `open_cdb_dump` invokes `cdb.exe` with default parameters which attempts online Microsoft Symbol Server queries (`_NT_SYMBOL_PATH`) that can hang or take several minutes on slow connections or when analyzing large module lists. |
| **Fix / Remediation** | Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` enforces offline-first local `C:\Symbols` resolution and `CDB_DUMP_OPEN_TIMEOUT = 25` prevents 3-minute hangs. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"dump_path": "games\\client\\logs\\crash\\minidump_20261011_194412.dmp", "include_modules": true, "include_stack_trace": true}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool open_cdb_dump
```

## Prevention Invariant
Patched in `mcp_windbg/server.py`: `_sanitize_offline_symbols` enforces offline-first local `C:\Symbols` resolution and `CDB_DUMP_OPEN_TIMEOUT = 25` prevents 3-minute hangs.
