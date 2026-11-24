---
id: MCP-255
server: kongor
tool: kongor_health_check
category: false-positive
title: "Reports client running true on hon_x64.exe -dedicated server workers"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-01
last_seen: 2026-11-01
tags: [kongor, kongor_health_check, false-positive]
prevention_rule: "Cross-check kongor_game_client_manage(status) rather than relying on kongor_health_check client.running when COMPEL hon_x64.exe -dedicated workers are active; filter out -dedicated command lines in HealthCheckHandler."
---

# MCP-255: Reports client running true on hon_x64.exe -dedicated server workers

## Summary
When `COMPEL` dedicated server workers (`games/hon server/hon_x64.exe -dedicated -noconfig ...`) were running while zero interactive/offscreen game clients were launched, `kongor` / `kongor_health_check` falsely reported `client.running: true` because both the K2 client and Windows dedicated server share the `hon_x64.exe` process image name. Tracked as `open`; callers should verify `kongor_game_client_manage(action: "status")` (`ipcConnected` and non-`-dedicated` PID filtering) until `HealthCheckHandler` filters `-dedicated` processes.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_health_check` |
| **Expected** | Report `client.running: false` when all running `hon_x64.exe` processes are dedicated server workers (`-dedicated` command line or `games/hon server/` / `games/server/` executable path). |
| **Actual (Symptom)** | Returns `"client": {"running": true, "pid": 38412, "ipcConnected": false}` pointing at a COMPEL `Slave-1` dedicated server worker PID while no game client is open. |
| **Root Cause** | `HealthCheckHandler` (`src/tools/kongor-cli/Mcp/Handlers/HealthCheckHandler.cs`) checks `Process.GetProcessesByName("hon_x64")` without excluding processes whose `MainModule.FileName` resides under `games/hon server` / `games/server` or whose Win32 command line contains `-dedicated` / `-masterserver`, unlike `GameClientManageHandler` which filters out dedicated server workers. |
| **Fix / Remediation** | 1. Reuse `GameClientManageHandler`'s dedicated-server command-line/path exclusion predicate inside `HealthCheckHandler` when populating `client.running` and `client.pid`.<br>2. Cross-check `kongor_game_client_manage(action: "status")` before assuming a player game client is running when COMPEL is active. |

## Invocation Parameters & Raw Logs
```json
{}
```
```json
{
  "status": "healthy",
  "client": {
    "running": true,
    "pid": 38412,
    "ipcConnected": false
  },
  "compel": {
    "running": true,
    "activeWorkers": 2
  }
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_health_check
```

## Prevention Invariant
Cross-check `kongor_game_client_manage(action: "status")` rather than relying on `kongor_health_check` `client.running` when COMPEL `hon_x64.exe -dedicated` workers are active; filter out `-dedicated` command lines and server directories in `HealthCheckHandler`.
