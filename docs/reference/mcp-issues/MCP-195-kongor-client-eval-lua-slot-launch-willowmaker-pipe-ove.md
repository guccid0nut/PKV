---
id: MCP-195
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-SLOT-LAUNCH-WILLOWMAKER-PIPE-OVERRIDE"
status: superseded
superseded_by: "MCP-136"
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [kongor, kongor_client_eval_lua, ipc-timeout]
prevention_rule: "Use `src/tools/tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` (which initializes a fresh `GameIpcBridge` targeting `kongor-native-client-{pid}`) or pass `slot: 0` with native pipe resolution."
---

# MCP-195: KONGOR-CLIENT-EVAL-LUA-SLOT-LAUNCH-WILLOWMAKER-PIPE-OVERRIDE

## Summary
Repeat occurrence of [`MCP-136`] on `kongor` / `kongor_client_eval_lua` (`ipc-timeout`). Superseded by `MCP-136`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate Lua string synchronously over `\\.\pipe\kongor-native-client-{pid}`. |
| **Actual (Symptom)** | Returned `IPC request 'ExecuteCommand' (RequestId: 40) timed out after 3000ms` because `GameClientManageHandler` reconnected the singleton `_ipcBridge` to `instance.PipeName` (`"willowmaker-ipc"`) during `action: "launch"`. |
| **Root Cause** | `ClientSlotInstance.PipeName` defaults to `"willowmaker-ipc"` for slot 0, and `GameClientManageHandler` called `_ipcBridge.ConfigurePipeName(instance.PipeName)`, overwriting `"kongor-native-client"` on the long-lived MCP server's `_ipcBridge` instance. |
| **Fix / Remediation** | Use `src/tools/tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` (which initializes a fresh `GameIpcBridge` targeting `kongor-native-client-{pid}`) or pass `slot: 0` with native pipe resolution. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return tostring(GetCvarString('host_version'))"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Use `src/tools/tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` (which initializes a fresh `GameIpcBridge` targeting `kongor-native-client-{pid}`) or pass `slot: 0` with native pipe resolution.
