---
id: MCP-242
server: kongor
tool: kongor_game_client_manage
category: schema-mismatch
title: "KONGOR-GAME-CLIENT-MANAGE-START-ACTION-SILENT-STATUS-FALLTHROUGH"
status: resolved
verified_by: "McpToolContractTests.Mcp241_GameClientManageHandler_AcceptsStartAlias_AndRejectsUnknownActionsWithoutSilentStatusFallthrough; logs/shj/mcp194_cold_boot_5x_summary.json; commit aca377a7b"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-24
last_seen: 2026-10-24
tags: [kongor, kongor_game_client_manage, schema-mismatch]
prevention_rule: "Use action 'launch' (or 'start', now aliased to 'launch' in GameClientManageHandler) to spawn hon_x64.exe, and verify status != 'error' before waiting on ipc_ready."
---

# MCP-242: KONGOR-GAME-CLIENT-MANAGE-START-ACTION-SILENT-STATUS-FALLTHROUGH

## Summary
During cold-boot canary automation (`scripts/kongor_shj.ps1 -Action cold-boot-canary -AllowClientRestart`), invoking `kongor` / `kongor_game_client_manage` with `{"action":"start","window_mode":"offscreen"}` (matching `kongor_server_manage`'s `"start"` action verb) silently fell through `default:` in `GameClientManageHandler.HandleAsync` (`GameClientManageHandler.cs:559-563`) to `GetClientStatusAsync`, returning `status: "success"`, `isError: false`, `running: false` in `385ms` without spawning `hon_x64.exe` and causing downstream `kongor_wait_for(ipc_ready)` to time out after `60s`. Fixed by aliasing `case "start":` to `case "launch":` in `GameClientManageHandler` and `GameManageHandler` and returning an explicit `status: "error"` diagnostic on unknown actions.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | `{"action":"start","window_mode":"offscreen"}` either spawns `hon_x64.exe` identically to `"launch"` or returns an explicit unknown-action error instead of a false-green status response. |
| **Actual (Symptom)** | Returned `{"status":"success","action":"status","running":false,"isError":false}` in `385ms`; `hon_x64.exe` was never started, causing `kongor_wait_for(condition="ipc_ready")` to fail after `60009.11ms`. |
| **Root Cause** | `GameClientManageHandler.cs:235` only matched `case "launch":` while `kongor_server_manage` used `case "start":`, and `GameClientManageHandler.cs:560` grouped `case "status":` with `default:`, turning any unrecognized action string into a `status: "success"` false-green. |
| **Fix / Remediation** | Added `case "start":` alongside `case "launch":` in `GameClientManageHandler.cs` and `GameManageHandler.cs`, separated `default:` from `case "status":` so unknown actions return `status: "error"` with valid action hints, routed `GameClientManageHandler.Launch.cs` executable resolution through injected `_processSupervisor`, and updated `scripts/kongor_shj.ps1` to pass `action: "launch"` and assert `status != "error"`. |

## Invocation Parameters & Raw Logs
```text
Request:
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"kongor_game_client_manage","arguments":{"action":"start","window_mode":"offscreen"}}}

Symptom:
[SHJ-CANARY] Stage 'ipc_ready' failed: Wait timed out after 60009.11ms ... observedState: { running: false, ipcConnected: false }
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Use action 'launch' (or 'start', now aliased to 'launch' in GameClientManageHandler) to spawn hon_x64.exe, and verify status != 'error' before waiting on ipc_ready.
