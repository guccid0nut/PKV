---
id: MCP-150
server: kongor
tool: kongor_crash_dump_analyze
category: transport-eof
title: "(formerly MCP-087): KONGOR-CRASH-DUMP-ANALYZE-EOF-ON-CLOSED-TRANSPORT"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_crash_dump_analyze, transport-eof]
prevention_rule: "Use `windbg` MCP tools (`open_cdb_dump`, `run_cdb_command`) or PowerShell with CDB directly to inspect crash dumps."
---

# MCP-150: (formerly MCP-087): KONGOR-CRASH-DUMP-ANALYZE-EOF-ON-CLOSED-TRANSPORT

## Summary
Tracked MCP issue on `kongor` / `kongor_crash_dump_analyze` (category: `transport-eof`, status: `resolved`, occurrences: 1). Automated crash dump analysis from native crash triage tool. -> `connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_crash_dump_analyze` |
| **Expected** | Automated crash dump analysis from native crash triage tool. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The kongor MCP server process stdio transport was closed/terminated in the environment. |
| **Fix / Remediation** | Use `windbg` MCP tools (`open_cdb_dump`, `run_cdb_command`) or PowerShell with CDB directly to inspect crash dumps. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"dump_path": "games\\client\\logs\\crash\\minidump_20261007_092202.dmp"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_crash_dump_analyze
```

## Prevention Invariant
Use `windbg` MCP tools (`open_cdb_dump`, `run_cdb_command`) or PowerShell with CDB directly to inspect crash dumps.
