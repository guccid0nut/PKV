---
id: MCP-113
server: supabase
tool: execute_sql
category: runtime-error
title: "`supabase` / `execute_sql` PostgreSQL Column Not Found `Username` in `core.'Users'`"
status: superseded
superseded_by: "MCP-104"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, runtime-error]
prevention_rule: "Always query `information_schema.columns` before assuming table column names."
---

# MCP-113: `supabase` / `execute_sql` PostgreSQL Column Not Found `Username` in `core.'Users'`

## Summary
Repeat occurrence of [`MCP-104`] on `supabase` / `execute_sql` (`runtime-error`). Superseded by `MCP-104`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Retrieve password hashes for users. |
| **Actual (Symptom)** | Failed with `42703: column "Username" does not exist in table core."Users"`. |
| **Root Cause** | In `core."Users"`, the user identifier column is named `Name` or `Email` or similar rather than `Username`. |
| **Fix / Remediation** | Inspect `information_schema.columns` for `core."Users"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "project_id": "ozobzkxiihvpcvokwzbm",
    "query": "SELECT \"ID\", \"Username\", \"SRPPasswordSalt\", \"SRPPasswordHash\", \"PBKDF2PasswordHash\" FROM core.\"Users\" WHERE \"Username\" IN ('guccidonut', 'PartyHost', 'PartyGuest');"
  }
  

Logs:

  Failed to run sql query: ERROR: 42703: column "Username" does not exist
  LINE 1: SELECT "ID", "Username", "SRPPasswordSalt", "SRPPasswordHash", "PBKDF2PasswordHash" FROM core."Users" WHERE "Username" IN ('guccidonut', 'PartyHost', 'PartyGuest');
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Always query `information_schema.columns` before assuming table column names.
