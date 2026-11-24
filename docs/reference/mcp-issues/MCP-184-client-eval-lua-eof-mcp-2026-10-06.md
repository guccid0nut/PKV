---
id: MCP-184
server: kongor
tool: kongor_client_eval_lua
category: transport-eof
title: "CLIENT-EVAL-LUA-EOF (MCP-2026-10-06)"
status: superseded
superseded_by: "MCP-149"
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [kongor, kongor_client_eval_lua, transport-eof]
prevention_rule: "Use `scratch/hon_ipc.py` directly for reliable IPC communication with `hon_x64` when `kongor` MCP server stdio transport is inactive."
---

# MCP-184: CLIENT-EVAL-LUA-EOF (MCP-2026-10-06)

## Summary
Repeat occurrence of [`MCP-149`] on `kongor` / `kongor_client_eval_lua` (`transport-eof`). Superseded by `MCP-149`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Execute Lua statement against live hon_x64 instance over MCP pipe. |
| **Actual (Symptom)** | Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The background `kongor` MCP server stdio process was closed/exited, preventing MCP tool invocations from reaching the MCP daemon. |
| **Fix / Remediation** | Use `scratch/hon_ipc.py` directly for reliable IPC communication with `hon_x64` when `kongor` MCP server stdio transport is inactive. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action":"eval","code":"return tostring(GetCvarString('host_version'))"}`
* **Expected**: Execute Lua statement against live hon_x64 instance over MCP pipe.
* **Actual**: Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.
* **Root Cause**: The background `kongor` MCP server stdio process was closed/exited, preventing MCP tool invocations from reaching the MCP daemon.
* **Status**: `[RESOLVED]` (Utilizing direct named pipe IPC runner `scratch/hon_ipc.py` connected to `\\.\pipe\kongor-native-client` which operates with sub-5ms latency and full bidirectional support).
* **Next-Time Prevention Rule**: Use `scratch/hon_ipc.py` directly for reliable IPC communication with `hon_x64` when `kongor` MCP server stdio transport is inactive.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Use `scratch/hon_ipc.py` directly for reliable IPC communication with `hon_x64` when `kongor` MCP server stdio transport is inactive.
