---
id: MCP-149
server: kongor
tool: kongor_client_eval_lua
category: transport-eof
title: "(formerly MCP-086): KONGOR-CLIENT-EVAL-LUA-EOF-ON-RELOAD-DISPATCH"
status: resolved
verified_by: "McpToolContractTests.MCP136_And_MCP149_And_MCP215_ClientEvalLuaHandler_EvaluatesSyncLua_HandlesReloadUiResiliently_AndEnrichesOfflineErrors"
superseded_by: ""
supersedes: ["MCP-182", "MCP-184", "MCP-152", "MCP-153", "MCP-155", "MCP-156", "MCP-158"]
occurrences: 8
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_client_eval_lua, transport-eof]
prevention_rule: "For UI control and IPC commands, use `scripts/reload_ui.ps1` or direct NamedPipe PowerShell script rather than relying exclusively on the external MCP daemon transport if it encounters an EOF disconnect."
---

# MCP-149: (formerly MCP-086): KONGOR-CLIENT-EVAL-LUA-EOF-ON-RELOAD-DISPATCH

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `transport-eof`, status: `resolved`, occurrences: 8). Tool dispatches Lua snippet to dismiss error dialog. -> `connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Tool dispatches Lua snippet to dismiss error dialog. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The `kongor` MCP server child process terminated or disconnected its stdio transport channel. |
| **Fix / Remediation** | For UI control and IPC commands, use `scripts/reload_ui.ps1` or direct NamedPipe PowerShell script rather than relying exclusively on the external MCP daemon transport if it encounters an EOF disconnect. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "local d = GetWidget('login_error_dialog')..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
For UI control and IPC commands, use `scripts/reload_ui.ps1` or direct NamedPipe PowerShell script rather than relying exclusively on the external MCP daemon transport if it encounters an EOF disconnect.
