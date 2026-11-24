---
id: MCP-066
server: supabase
tool: execute_sql
category: parameter-mismatch
title: "`supabase` execute_sql Validation Error: Missing Required Argument `project_id`"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-105", "MCP-109", "MCP-110"]
occurrences: 4
date: 2026-09-25
last_seen: 2026-09-26
tags: [supabase, execute_sql, parameter-mismatch]
prevention_rule: "Pass `project_id: 'ozobzkxiihvpcvokwzbm'` on all Supabase tool calls."
---

# MCP-066: `supabase` execute_sql Validation Error: Missing Required Argument `project_id`

## Summary
Tracked MCP issue on `supabase` / `execute_sql` (category: `parameter-mismatch`, status: `resolved`, occurrences: 4). Execute SQL query on active Supabase database. -> Failed with ZodError: `Invalid input: expected string, received undefined` at `project_id`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `supabase` / `execute_sql` |
| **Expected** | Execute SQL query on active Supabase database. |
| **Actual (Symptom)** | Failed with ZodError: `Invalid input: expected string, received undefined` at `project_id`. |
| **Root Cause** | The Supabase MCP server requires `project_id` on every tool call even when only a single project (`ozobzkxiihvpcvokwzbm`) exists in the organization. |
| **Fix / Remediation** | Provided `project_id: "ozobzkxiihvpcvokwzbm"` on subsequent calls. Query succeeded. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "SELECT a.\"ID\", a.\"Name\", a.\"Type\" FROM \"Accounts\" a WHERE a.\"Name\" ILIKE '%gucci%';"
  }
  

Logs:

  Encountered error in tool execution: {"error":{"name":"ZodError","message":"[\n  {\n    \"expected\": \"string\",\n    \"code\": \"invalid_type\",\n    \"path\": [\n      \"project_id\"\n    ],\n    \"message\": \"Invalid input: expected string, received undefined\"\n  }\n]"}}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server supabase -Tool execute_sql
```

## Prevention Invariant
Pass `project_id: 'ozobzkxiihvpcvokwzbm'` on all Supabase tool calls.
