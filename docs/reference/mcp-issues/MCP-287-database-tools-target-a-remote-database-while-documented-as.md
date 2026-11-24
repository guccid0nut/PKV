---
id: MCP-287
server: kongor
tool: kongor_db_pool_diagnostics
category: configuration
title: "Database tools target a remote database while documented as local"
status: resolved
verified_by: "b5f8259 + McpToolContractTests.Tranche1B + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_db_pool_diagnostics, configuration]
prevention_rule: "Database tools must print the resolved endpoint, refuse mutating actions on non-loopback hosts without explicit confirmation, and describe the real default."
---

# MCP-287: Database tools target a remote database while documented as local

## Summary
`kongor_diagnostics`, `kongor_server_manage status` and `kongor_db_pool_diagnostics` connected from the MCP process to a hosted (non-loopback) PostgreSQL pooler endpoint, while the database tool descriptions speak of a local development database. Found in the 2026-11-17 live audit while every local service was stopped. Open; no fix applied. Mutating database actions were deliberately not run.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_db_pool_diagnostics` |
| **Expected** | Read-only probes state which endpoint they contact; tools described as local use a loopback database by default. |
| **Actual (Symptom)** | With no local database running, `kongor_diagnostics` reported the database `connected: true` (about 90 ms) and `kongor_db_pool_diagnostics status` / `pool_metrics` reported a hosted Supabase pooler endpoint on port 6543, database `postgres`, health `Healthy`. `kongor_db_fixture status` and `kongor_db_migration_test status` refused to run without `connectionString` or `KONGOR_DB_CONNECTION_STRING`, so the tools disagree on which database is configured. |
| **Root Cause** | Not traced in source. The diagnostics path resolves a connection string from configuration that the fixture and migration tools do not use. |
| **Fix / Remediation** | Not fixed. Until fixed, treat every `kongor_db_*` action and `kongor_cache_coherence_verify heal` as acting on the hosted database unless a loopback connection string is passed explicitly. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 283, 284 kongor_db_pool_diagnostics {status / pool_metrics} -> provider Npgsql, hosted pooler endpoint :6543, health Healthy
rows 286, 287 kongor_db_fixture / kongor_db_migration_test {status} -> isError: requires 'connectionString' or KONGOR_DB_CONNECTION_STRING
row 47 kongor_diagnostics {} -> database connected true while no local database was running
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_db_pool_diagnostics
```

## Prevention Invariant
Database tools must print the resolved endpoint, refuse mutating actions on non-loopback hosts without explicit confirmation, and describe the real default.
