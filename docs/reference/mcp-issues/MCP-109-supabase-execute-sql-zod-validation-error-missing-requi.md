---
id: MCP-109
server: supabase
tool: execute_sql
category: parameter-mismatch
title: "`supabase` / `execute_sql` Zod Validation Error: Missing Required Parameter `project_id`"
status: superseded
superseded_by: "MCP-066"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [supabase, execute_sql, parameter-mismatch]
prevention_rule: "Always consult tool schemas in `mcp/supabase/*.json` before calling. Always supply `project_id` (e.g. `ozobzkxiihvpcvokwzbm`)."
---

# MCP-109: `supabase` / `execute_sql` Zod Validation Error: Missing Required Parameter `project_id`

## Summary
Repeat occurrence of [`MCP-066`] on `supabase` / `execute_sql` (`parameter-mismatch`). Superseded by `MCP-066`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Execute raw SQL query against Postgres database. |
| **Actual (Symptom)** | Failed with `ZodError: Invalid input: expected string, received undefined at path ['project_id']`. |
| **Root Cause** | The `supabase / execute_sql` tool schema requires `project_id` as a required parameter alongside `query`. The project ID was omitted in the invocation. |
| **Fix / Remediation** | Include `project_id: "ozobzkxiihvpcvokwzbm"` in subsequent `execute_sql` calls. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "SELECT \"ID\", \"Nickname\", \"Email\" FROM core.\"Accounts\" LIMIT 10;"
  }
  

Logs:

  {"error":{"name":"ZodError","message":"[\n  {\n    \"expected\": \"string\",\n    \"code\": \"invalid_type\",\n    \"path\": [\n      \"project_id\"\n    ],\n    \"message\": \"Invalid input: expected string, received undefined\"\n  }\n]"}}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Always consult tool schemas in `mcp/supabase/*.json` before calling. Always supply `project_id` (e.g. `ozobzkxiihvpcvokwzbm`).
