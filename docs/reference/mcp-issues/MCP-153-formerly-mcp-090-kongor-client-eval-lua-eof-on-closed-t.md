---
id: MCP-153
server: kongor
tool: kongor_client_eval_lua
category: transport-eof
title: "(formerly MCP-090): KONGOR-CLIENT-EVAL-LUA-EOF-ON-CLOSED-TRANSPORT"
status: superseded
superseded_by: "MCP-149"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_client_eval_lua, transport-eof]
prevention_rule: "Use PowerShell named pipe script (`scripts/reload_ui.ps1` or `scripts/test_ipc.ps1`) to interact with Willowmaker IPC / client directly."
---

# MCP-153: (formerly MCP-090): KONGOR-CLIENT-EVAL-LUA-EOF-ON-CLOSED-TRANSPORT

## Summary
Repeat occurrence of [`MCP-149`] on `kongor` / `kongor_client_eval_lua` (`transport-eof`). Superseded by `MCP-149`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Runs Lua script in running client. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The `kongor` MCP server child process has closed transport or terminated stdio connection. |
| **Fix / Remediation** | Use PowerShell named pipe script (`scripts/reload_ui.ps1` or `scripts/test_ipc.ps1`) to interact with Willowmaker IPC / client directly. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"lua_code": "dofile('ClientPackageFiles/ui/scripts/dump_ui.lua')"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Use PowerShell named pipe script (`scripts/reload_ui.ps1` or `scripts/test_ipc.ps1`) to interact with Willowmaker IPC / client directly.
