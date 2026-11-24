---
id: MCP-166
server: windbg
tool: run_cdb_command
category: parameter-mismatch
title: "(formerly MCP-103): WINDBG-RUN-CDB-COMMAND-PARAM-CASING-MISMATCH"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-08
last_seen: 2026-11-04
tags: [windbg, run_cdb_command, parameter-mismatch]
prevention_rule: "`_normalize_windbg_arguments` in `mcp_windbg/server.py` now automatically normalizes PascalCase, camelCase, and alias parameter names (`SessionId` -> `session_id`, `Command` -> `command`, `DumpPath` -> `dump_path`)."
---

# MCP-166: (formerly MCP-103): WINDBG-RUN-CDB-COMMAND-PARAM-CASING-MISMATCH

## Summary
Tracked MCP issue on `windbg` / `run_cdb_command` (category: `parameter-mismatch`, status: `investigating`, occurrences: 2). Executes CDB command and returns output. -> Pydantic validation error: `session_id: Field required`, `command: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `windbg` / `run_cdb_command` |
| **Expected** | Executes CDB command and returns output. |
| **Actual (Symptom)** | Pydantic validation error: `session_id: Field required`, `command: Field required`. |
| **Root Cause** | Caller passed PascalCase parameters (`SessionId`, `Command`) instead of lowercase snake_case parameters (`session_id`, `command`). |
| **Fix / Remediation** | `_normalize_windbg_arguments` in `mcp_windbg/server.py` now automatically normalizes PascalCase, camelCase, and alias parameter names (`SessionId` -> `session_id`, `Command` -> `command`, `DumpPath` -> `dump_path`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"SessionId": "cdb-2c730140", "Command": "!analyze -v"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server windbg -Tool run_cdb_command
```

## Prevention Invariant
`_normalize_windbg_arguments` in `mcp_windbg/server.py` now automatically normalizes PascalCase, camelCase, and alias parameter names (`SessionId` -> `session_id`, `Command` -> `command`, `DumpPath` -> `dump_path`).
