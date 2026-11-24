---
id: MCP-111
server: supabase
tool: execute_sql
category: runtime-error
title: "`supabase` / `execute_sql` PostgreSQL Column Not Found `Nickname` in `core.'Accounts'`"
status: superseded
superseded_by: "MCP-104"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, runtime-error]
prevention_rule: "Inspect `information_schema.columns` before assuming case-sensitive quoted identifier names."
---

# MCP-111: `supabase` / `execute_sql` PostgreSQL Column Not Found `Nickname` in `core.'Accounts'`

## Summary
Repeat occurrence of [`MCP-104`] on `supabase` / `execute_sql` (`runtime-error`). Superseded by `MCP-104`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Retrieve account rows matching nickname. |
| **Actual (Symptom)** | Failed with `42703: column "Nickname" does not exist in table core."Accounts"`. |
| **Root Cause** | The column name for account nickname in `core."Accounts"` is named differently (e.g. `Name` or lowercase). |
| **Fix / Remediation** | Query `information_schema.columns` for exact column naming. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "project_id": "ozobzkxiihvpcvokwzbm",
    "query": "SELECT \"ID\", \"UserID\", \"Nickname\" FROM core.\"Accounts\" WHERE \"Nickname\" IN ('PartyHost', 'PartyGuest');"
  }
  

Logs:

  Failed to run sql query: ERROR: 42703: column "Nickname" does not exist
  LINE 1: SELECT "ID", "UserID", "Nickname" FROM core."Accounts" WHERE "Nickname" IN ('PartyHost', 'PartyGuest');
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Inspect `information_schema.columns` before assuming case-sensitive quoted identifier names.
