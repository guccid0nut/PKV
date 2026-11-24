---
id: MCP-000
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "<Short descriptive title>"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-16
last_seen: 2026-10-16
tags: [kongor, ipc]
prevention_rule: "<One-sentence actionable rule to succeed on the first attempt next time>"
---

# MCP-000: <Short Descriptive Title>

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Actionable rule to follow before invoking this MCP tool so this failure never recurs.
