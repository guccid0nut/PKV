---
id: MCP-110
server: supabase
tool: execute_sql
category: parameter-mismatch
title: "`supabase` / `execute_sql` PostgreSQL Column Not Found `Nickname` in `core.'Users'`"
status: superseded
superseded_by: "MCP-066"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, parameter-mismatch]
prevention_rule: "Query `core.'Accounts'` for player nicknames or join `core.'Accounts'` on `UserID` with `core.'Users'`."
---

# MCP-110: `supabase` / `execute_sql` PostgreSQL Column Not Found `Nickname` in `core.'Users'`

## Summary
Repeat occurrence of [`MCP-066`] on `supabase` / `execute_sql` (`parameter-mismatch`). Superseded by `MCP-066`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Retrieve User rows matching nicknames. |
| **Actual (Symptom)** | Failed with `42703: column "Nickname" does not exist in table core."Users"`. |
| **Root Cause** | In the Kongor database schema, `Nickname` belongs to `core."Accounts"`, whereas `core."Users"` uses `Username`. |
| **Fix / Remediation** | Correct query to target `core."Accounts"` or join `core."Users"` on `Username`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "project_id": "ozobzkxiihvpcvokwzbm",
    "query": "SELECT \"ID\", \"Nickname\", \"Email\" FROM core.\"Users\" WHERE \"Nickname\" IN ('PartyHost', 'PartyGuest');"
  }
  

Logs:

  Failed to run sql query: ERROR: 42703: column "Nickname" does not exist
  LINE 1: SELECT "ID", "Nickname", "Email" FROM core."Users" WHERE "Nickname" IN ('PartyHost', 'PartyGuest');
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Query `core.'Accounts'` for player nicknames or join `core.'Accounts'` on `UserID` with `core.'Users'`.
