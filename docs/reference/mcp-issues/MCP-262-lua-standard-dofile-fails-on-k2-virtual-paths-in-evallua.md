---
id: MCP-262
server: kongor
tool: kongor_client_eval_lua
category: lua-eval
title: "Lua standard dofile fails on K2 virtual paths in EvalLua"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-06
last_seen: 2026-11-06
tags: [kongor, kongor_client_eval_lua, lua-eval]
prevention_rule: "Use dofile('override/ui/scripts/...') relative to games/client CWD or ReloadInterface('main') instead of dofile('/ui/...') which bypasses K2 VFS."
---

# MCP-262: Lua standard dofile fails on K2 virtual paths in EvalLua

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | What the tool call was expected to return or execute. |
| **Actual (Symptom)** | Exact error message, timeout duration, or unexpected response returned. |
| **Root Cause** | Technical explanation of why the MCP tool or transport failed. |
| **Fix / Remediation** | Code patch, wrapper normalization, or workflow fallback applied. |

## Invocation Parameters & Raw Logs
```text
<Exact JSON parameters passed and verbatim error log / stderr / NDJSON snippet>
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Use dofile('override/ui/scripts/...') relative to games/client CWD or ReloadInterface('main') instead of dofile('/ui/...') which bypasses K2 VFS.
