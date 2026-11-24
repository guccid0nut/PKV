---
id: MCP-197
server: kongor
tool: kongor_game_telemetry
category: ipc-timeout
title: "KONGOR-GAME-TELEMETRY-SHMEM-FALLBACK-PIPE-CHECK-DURING-COLD-BOOT"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [kongor, kongor_game_telemetry, ipc-timeout]
prevention_rule: "Verify client readiness via `tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` after cold boot completes (~18s)."
---

# MCP-197: KONGOR-GAME-TELEMETRY-SHMEM-FALLBACK-PIPE-CHECK-DURING-COLD-BOOT

## Summary
Tracked MCP issue on `kongor` / `kongor_game_telemetry` (category: `ipc-timeout`, status: `resolved`, occurrences: 1). Read `Local\KongorClientTelemetry_{pid}` shared memory snapshot during cold boot. -> Returned `Telemetry query error: Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable` while `hon_x64.exe` (`PID 22948`) was still loading `k2_x64.dll` interfaces (+15s into cold boot).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_telemetry` |
| **Expected** | Read `Local\KongorClientTelemetry_{pid}` shared memory snapshot during cold boot. |
| **Actual (Symptom)** | Returned `Telemetry query error: Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable` while `hon_x64.exe` (`PID 22948`) was still loading `k2_x64.dll` interfaces (+15s into cold boot). |
| **Root Cause** | The long-lived MCP server process (`kongor`) had `_ipcBridge.PipeName` overwritten to `"willowmaker-ipc"` by `GameClientManageHandler` during `action: "launch"`, causing `GameTelemetryHandler`'s IPC fallback check to fail before `Local\KongorClientTelemetry_22948` finished initializing. |
| **Fix / Remediation** | Verify client readiness via `tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` after cold boot completes (~18s). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "snapshot", "channel": "shmem"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_telemetry
```

## Prevention Invariant
Verify client readiness via `tao_mcp.py --tool kongor_client_eval_lua --args-b64 <base64>` after cold boot completes (~18s).
