---
id: MCP-264
server: kongor
tool: kongor_game_server_manage
category: port-override
title: "launch binds svr_port from startup.cfg instead of port 11235 and rejects unarranged Connect"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-09
last_seen: 2026-11-09
tags: [kongor, kongor_game_server_manage, port-override]
prevention_rule: "Use kongor_compel_manage(action: 'start') to spawn arranged dedicated server workers registered with chat-server:11031 instead of raw kongor_game_server_manage(action: 'launch')."
---

# MCP-264: launch binds svr_port from startup.cfg instead of port 11235 and rejects unarranged Connect

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_manage` |
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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_manage
```

## Prevention Invariant
Use kongor_compel_manage(action: 'start') to spawn arranged dedicated server workers registered with chat-server:11031 instead of raw kongor_game_server_manage(action: 'launch').
