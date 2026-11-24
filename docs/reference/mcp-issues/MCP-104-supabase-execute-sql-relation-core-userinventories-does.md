---
id: MCP-104
server: supabase
tool: execute_sql
category: runtime-error
title: "`supabase` / `execute_sql` Relation 'core.UserInventories' Does Not Exist"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-111", "MCP-113"]
occurrences: 3
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, runtime-error]
prevention_rule: "Query `information_schema.tables` first or use canonical singular name `core.'UserInventory'`."
---

# MCP-104: `supabase` / `execute_sql` Relation 'core.UserInventories' Does Not Exist

## Summary
Tracked MCP issue on `supabase` / `execute_sql` (category: `runtime-error`, status: `resolved`, occurrences: 3). Retrieve inventory rows for User ID 3. -> Failed with `HttpException: ERROR: 42P01: relation "core.UserInventories" does not exist`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Retrieve inventory rows for User ID 3. |
| **Actual (Symptom)** | Failed with `HttpException: ERROR: 42P01: relation "core.UserInventories" does not exist`. |
| **Root Cause** | Entity Framework entity is named `UserInventory`, mapped to table `core."UserInventory"` (singular), not plural `UserInventories`. |
| **Fix / Remediation** | Corrected table reference to `core."UserInventory"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "project_id": "ozobzkxiihvpcvokwzbm",
    "query": "SELECT * FROM core.\"UserInventories\" WHERE \"UserID\" = 3 LIMIT 5;"
  }
  

Logs:

  Failed to run sql query: ERROR: 42P01: relation "core.UserInventories" does not exist
  LINE 1: SELECT * FROM core."UserInventories" WHERE "UserID" = 3 LIMIT 5;
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Query `information_schema.tables` first or use canonical singular name `core.'UserInventory'`.
