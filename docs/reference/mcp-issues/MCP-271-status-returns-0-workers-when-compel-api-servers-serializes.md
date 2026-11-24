---
id: MCP-271
server: kongor
tool: kongor_game_server_manage
category: schema-mismatch
title: "status returns 0 workers when COMPEL /api/servers serializes running instead of isRunning or runs on :11484"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Handlers/GameServerManageHandler.cs"
verified_by: "src/tools/kongor-cli/Mcp/Handlers/GameServerManageHandler.cs"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-14
last_seen: 2026-11-14
tags: [kongor, kongor_game_server_manage, schema-mismatch]
prevention_rule: "Parse both 'running' and 'isRunning' (and 'tickrateHz'/'currentTickrateHz') in GameServerManageHandler.DiscoverCompelManagedServers, include port 11484 alongside 11234/11334/11434, and exclude games/client hon_x64.exe (willowmaker-ipc) from IsDedicatedServerProcess."
---

# MCP-271: status returns 0 workers when COMPEL /api/servers serializes running instead of isRunning or runs on :11484

## Summary
`kongor` MCP tool `kongor_game_server_manage` (`action: "status"`) returned `totalWorkers: 0` against active containerized COMPEL fleets (`:11334` Match Tier D and `:11484` Queue Arcade Tier Q) because `DiscoverCompelManagedServers` checked `isRunning` instead of `DedicatedWorkerStatus`'s serialized `running` JSON property and omitted `:11484`. Additionally, `IsDedicatedServerProcess` could misclassify a detached Borderless DXVK player client (`games/client/hon_x64.exe` with `willowmaker-ipc`) as a headless server when `MainWindowHandle == 0`. Both defects were fixed and codified in `GameServerManageHandler.cs`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_manage` |
| **Expected** | Return all 4 active containerized dedicated server workers (`11335`, `11336` on `:11334` and `11485`, `11486` on `:11484`) without misclassifying the player client. |
| **Actual (Symptom)** | Returned `{"status":"success","action":"status","totalWorkers":0,"running":false,"workers":[]}` before fix, and included `games/client/hon_x64.exe` (`willowmaker-ipc`) when `MainWindowHandle` was zero from a non-interactive child process. |
| **Root Cause** | `DedicatedWorkerStatus` serializes `"running"` and `"tickrateHz"`, whereas `DiscoverCompelManagedServers` only checked `"isRunning"` and `"currentTickrateHz"`, and `compelPorts` omitted `11484`. `IsDedicatedServerProcess` fell through to `MainWindowHandle == IntPtr.Zero` even when `cmdLine` showed a player client (`willowmaker-ipc` / `games\client\`). |
| **Fix / Remediation** | Updated `GameServerManageHandler.DiscoverCompelManagedServers` to accept `"running"`/`"isRunning"`, `"tickrateHz"`/`"currentTickrateHz"`, and port `11484`, and guarded `IsDedicatedServerProcess` against `willowmaker-ipc` / `games/client/` processes. |

## Invocation Parameters & Raw Logs
```text
Arguments: {"action":"status"}
Before fix: {"status":"success","action":"status","totalWorkers":0,"running":false,"workers":[]}
After fix: Discovers ports 11335, 11336 (:11334) and 11485, 11486 (:11484) cleanly.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_manage
```

## Prevention Invariant
Parse both 'running' and 'isRunning' (and 'tickrateHz'/'currentTickrateHz') in GameServerManageHandler.DiscoverCompelManagedServers, include port 11484 alongside 11234/11334/11434, and exclude games/client hon_x64.exe (willowmaker-ipc) from IsDedicatedServerProcess.
