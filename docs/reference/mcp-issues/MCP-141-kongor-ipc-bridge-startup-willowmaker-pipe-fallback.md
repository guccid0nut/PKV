---
id: MCP-141
server: kongor
tool: kongor_client_eval_lua
category: binary-decode
title: "KONGOR-IPC-BRIDGE-STARTUP-WILLOWMAKER-PIPE-FALLBACK"
status: resolved
verified_by: "McpToolContractTests.MCP138_And_MCP141_And_MCP142_RateLimitIsolation_NativePipeNoFallback_AndGracefulClientStatus"
superseded_by: ""
supersedes: []
occurrences: 7
date: 2026-10-13
last_seen: 2026-10-18
tags: [kongor, kongor_client_eval_lua, binary-decode]
prevention_rule: "Fixed in `GameIpcBridge.cs`: `HandleDisconnection` now calls `CleanupExistingConnection()`, and `ConnectAsync` never falls back to `'willowmaker-ipc'` when `PipeName` is `'kongor-native-client'`. Also wait ~5 seconds after `kongor_game_clie..."
---

# MCP-141: KONGOR-IPC-BRIDGE-STARTUP-WILLOWMAKER-PIPE-FALLBACK

## Summary
Tracked MCP issue on `kongor` / `kongor_client_eval_lua` (category: `binary-decode`, status: `resolved`, occurrences: 7). Connect to `\\.\pipe\kongor-native-client` and execute synchronous Lua evaluation / IPC engine state query. -> Timed out after 3000ms (`IPC request 'ExecuteCommand' (RequestId: 255) timed out after 3000ms`) when called immediately after `hon_x64.exe` restart.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Connect to `\\.\pipe\kongor-native-client` and execute synchronous Lua evaluation / IPC engine state query. |
| **Actual (Symptom)** | Timed out after 3000ms (`IPC request 'ExecuteCommand' (RequestId: 255) timed out after 3000ms`) when called immediately after `hon_x64.exe` restart. |
| **Root Cause** | Two bugs in `GameIpcBridge.cs`: (1) `HandleDisconnection` did not call `CleanupExistingConnection()`, leaving `_pipeClient` non-null after the previous `hon_x64.exe` process exited, and (2) `ConnectAsync` included `"willowmaker-ipc"` in `candidatePipes` even when `PipeName` was `"kongor-native-client"`, and overwrote `PipeName = "willowmaker-ipc"`... |
| **Fix / Remediation** | Fixed in `GameIpcBridge.cs`: `HandleDisconnection` now calls `CleanupExistingConnection()`, and `ConnectAsync` never falls back to `"willowmaker-ipc"` when `PipeName` is `"kongor-native-client"`. Also wait ~5 seconds after `kongor_game_client_manage action:launch` before issuing Lua eval calls so `k2_x64.dll` finishes initializing the Lua VM. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"code": "return collectgarbage(\"count\")"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Fixed in `GameIpcBridge.cs`: `HandleDisconnection` now calls `CleanupExistingConnection()`, and `ConnectAsync` never falls back to `'willowmaker-ipc'` when `PipeName` is `'kongor-native-client'`. Also wait ~5 seconds after `kongor_game_clie...
