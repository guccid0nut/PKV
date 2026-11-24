---
id: MCP-146
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "(formerly MCP-083): KONGOR-CLIENT-EVAL-LUA-RESPONSE-UNEXPECTED-ERROR-WRAPPER"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Verify kongor / kongor_client_eval_lua readiness and parameters before invocation."
---

# MCP-146: (formerly MCP-083): KONGOR-CLIENT-EVAL-LUA-RESPONSE-UNEXPECTED-ERROR-WRAPPER

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Tool returns structured success with evaluated Lua return value. |
| **Actual (Symptom)** | Tool returned status: "error" despite inner response payload having `{"success": true}`. |
| **Root Cause** | The NamedPipe bridge returned an inner JSON string rather than unwrapped response object, triggering error handling wrapper. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return 'Lua is alive...'"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Verify kongor / kongor_client_eval_lua readiness and parameters before invocation.
