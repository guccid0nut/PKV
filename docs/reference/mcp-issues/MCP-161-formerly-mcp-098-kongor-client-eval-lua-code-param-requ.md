---
id: MCP-161
server: kongor
tool: kongor_client_eval_lua
category: parameter-mismatch
title: "(formerly MCP-098): KONGOR-CLIENT-EVAL-LUA-CODE-PARAM-REQUIRED"
status: investigating
codified_in: "src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs"
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-07
last_seen: 2026-11-13
tags: [kongor, kongor_client_eval_lua, parameter-mismatch]
prevention_rule: "Always supply `code` as the snippet parameter for `kongor_client_eval_lua`."
---

# MCP-161: (formerly MCP-098): KONGOR-CLIENT-EVAL-LUA-CODE-PARAM-REQUIRED

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `parameter-mismatch`, status: `investigating`, occurrences: 4). Evaluates Lua snippet. -> Threw validation error: `Missing required parameter 'code' for action 'eval'`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluates Lua snippet. |
| **Actual (Symptom)** | Threw validation error: `Missing required parameter 'code' for action 'eval'`. |
| **Root Cause** | `kongor_client_eval_lua` expects parameter named `code` rather than `lua_code`. |
| **Fix / Remediation** | Always supply `code` as the snippet parameter for `kongor_client_eval_lua`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"lua_code": "return ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Always supply `code` as the snippet parameter for `kongor_client_eval_lua`.
