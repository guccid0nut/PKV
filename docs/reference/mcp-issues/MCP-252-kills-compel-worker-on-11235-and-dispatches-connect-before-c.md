---
id: MCP-252
server: kongor
tool: kongor_scrimmage_launch
category: port-collision
title: "Kills COMPEL worker on 11235 and dispatches connect before client engine_ready"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-01
last_seen: 2026-11-01
tags: [kongor, kongor_scrimmage_launch, port-collision]
prevention_rule: "Do not call kongor_scrimmage_launch on ports managed by a running COMPEL supervisor (11235-11260); either stop COMPEL first for raw scrimmage or host via COMPEL/Lobby_V2 after kongor_wait_for(engine_ready)."
---

# MCP-252: Kills COMPEL worker on 11235 and dispatches connect before client engine_ready

## Summary
Invoking `kongor` / `kongor_scrimmage_launch` with the default dedicated port `11235` while the COMPEL supervisor was actively managing worker instances in the `11235`–`11260` range terminated COMPEL's `Slave-1` worker (triggering a COMPEL auto-respawn race on UDP `11235`) and dispatched `Connect 127.0.0.1:11235` to `hon_x64.exe` before the client main loop reached `engine_ready`. Tracked as `open` with a port-isolation guardrail (`>= 11350`) and mandatory `kongor_wait_for(condition: "engine_ready")` gate before `Connect`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_scrimmage_launch` |
| **Expected** | Start an isolated local dedicated server on an unoccupied UDP port, wait until `hon_x64.exe` reaches `engine_ready`, and connect the client into the active scrimmage match. |
| **Actual (Symptom)** | Kills COMPEL's `hon_x64.exe -dedicated` worker on `11235`, collides with COMPEL's immediate worker respawn on UDP `11235`, and drops `Connect 127.0.0.1:11235` during cold client initialization so the client lands at the login/main screen (`gamePhase == 0`). |
| **Root Cause** | `ScrimmageLaunchHandler` (`src/tools/kongor-cli/Mcp/Handlers/ScrimmageLaunchHandler.cs`) defaults `port` to `11235` (COMPEL's primary `Slave-1` port) and cleans up existing listeners on that port without checking whether `COMPEL.exe` owns the port range, then sends `Connect` as soon as the client pipe opens (`ipc_ready`) rather than waiting for `hostFrame` advancement (`engine_ready`). |
| **Fix / Remediation** | 1. Allocate an unreserved scrimmage UDP port outside COMPEL's `11235`–`11260` pool (e.g. `11350+`) in `ScrimmageLaunchHandler` and await `engine_ready` (`hostFrame > firstHostFrame`) before issuing `Connect`.<br>2. Until patched, pass an explicit high port (`port: 11355`) or stop COMPEL before raw scrimmage launch, and poll `kongor_wait_for(condition: "engine_ready", timeoutMs: 25000)` prior to client connection. |

## Invocation Parameters & Raw Logs
```json
{
  "map": "caldavar",
  "port": 11235,
  "botCount": 9
}
```
```json
{
  "status": "warning",
  "port": 11235,
  "serverStarted": true,
  "clientConnected": false,
  "gamePhase": 0,
  "message": "Scrimmage server launched on 11235, but client did not transition to active match within timeout."
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_scrimmage_launch
```

## Prevention Invariant
Do not call `kongor_scrimmage_launch` on ports managed by a running COMPEL supervisor (`11235`–`11260`); either pass an isolated port (`11355+`), stop COMPEL first for raw scrimmage, or host via COMPEL/`Lobby_V2` after `kongor_wait_for(condition: "engine_ready")`.
