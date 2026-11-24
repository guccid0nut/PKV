---
id: MCP-105
server: supabase
tool: execute_sql
category: parameter-mismatch
title: "`supabase` / `execute_sql` Column 'AccountID' Does Not Exist in data.AccountStatistics"
status: superseded
superseded_by: "MCP-066"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, parameter-mismatch]
prevention_rule: "Check column names via `information_schema.columns` before querying `data.*` tables; use `account_id` instead of `'AccountID'`."
---

# MCP-105: `supabase` / `execute_sql` Column 'AccountID' Does Not Exist in data.AccountStatistics

## Summary
Repeat occurrence of [`MCP-066`] on `supabase` / `execute_sql` (`parameter-mismatch`). Superseded by `MCP-066`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Retrieve account statistics rows for account ID 59. |
| **Actual (Symptom)** | Failed with `HttpException: ERROR: 42703: column "AccountID" does not exist`. |
| **Root Cause** | The `data."AccountStatistics"` table schema uses PostgreSQL snake_case columns (`account_id`), unlike `core."Accounts"` which uses PascalCase. |
| **Fix / Remediation** | Corrected query column name to `account_id`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "project_id": "ozobzkxiihvpcvokwzbm",
    "query": "SELECT * FROM data.\"AccountStatistics\" WHERE \"AccountID\" = 59;"
  }
  

Logs:

  Failed to run sql query: ERROR: 42703: column "AccountID" does not exist
  LINE 1: SELECT * FROM data."AccountStatistics" WHERE "AccountID" = 59;
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Check column names via `information_schema.columns` before querying `data.*` tables; use `account_id` instead of `'AccountID'`.
