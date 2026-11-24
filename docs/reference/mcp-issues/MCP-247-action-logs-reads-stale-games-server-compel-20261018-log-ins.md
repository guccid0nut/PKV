---
id: MCP-247
server: kongor
tool: kongor_game_server_manage
category: stale-data
title: "action logs reads stale games/server COMPEL-20261018.log instead of live games/hon server Slave logs"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-26
last_seen: 2026-10-26
tags: [kongor, kongor_game_server_manage, stale-data]
prevention_rule: "For COMPEL workers read games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/Slave-N_DATE.log (UTF-16LE) or GET /instances/PORT/logs with Bearer token; check workingDirectory field in the response."
---

# MCP-247: action logs reads stale games/server COMPEL-20261018.log instead of live games/hon server Slave logs

## Summary
Calling `kongor` / `kongor_game_server_manage` with `action: "logs"` while investigating live dedicated server worker state returned historical lines from a stale `games/server/` `COMPEL-20261018.log` file rather than the active UTF-16LE `Slave-N_<date>.log` written by the running `COMPEL` worker under `games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/`. Tracked as `open` with a pre-flight check on `workingDirectory` and direct Everything / COMPEL HTTP API log inspection.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_manage` |
| **Expected** | Return recent UTF-16LE `Slave-N_<date>.log` lines emitted by the currently running dedicated server worker on the target UDP port (e.g. `11235`). |
| **Actual (Symptom)** | Returns `"status": "success"` with stale lines from `games/server/COMPEL-20261018.log` whose timestamps predate the current session by days, masking live server console output. |
| **Root Cause** | `GameServerManageHandler` searches static candidate directories (`games/server/` before `games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/`) without resolving the live `hon_x64.exe -dedicated` process working directory or sorting across both roots by `LastWriteTimeUtc` with UTF-16LE BOM decoding. |
| **Fix / Remediation** | 1. Update `GameServerManageHandler` (`src/tools/kongor-cli/Mcp/Handlers/GameServerManageHandler.cs`) to resolve the active worker's working directory and select the newest `Slave-*.log` / `console.log` by `LastWriteTimeUtc` using UTF-16LE encoding detection.<br>2. Until patched, locate the newest `Slave-*.log` via Everything MCP (`ext:log Slave-`) under `games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/` or query COMPEL's `/instances/{port}/logs` endpoint. |

## Invocation Parameters & Raw Logs
```json
{
  "action": "logs",
  "port": 11235,
  "tailLines": 50
}
```
```json
{
  "status": "success",
  "action": "logs",
  "port": 11235,
  "logPath": "games/server/COMPEL-20261018.log",
  "lines": [
    "[2026-10-18 14:22:09] [COMPEL] Supervisor initialized (stale log)"
  ]
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_manage
```

## Prevention Invariant
For COMPEL workers read `games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/Slave-N_DATE.log` (UTF-16LE) or `GET /instances/PORT/logs` with Bearer token; check `workingDirectory` and `logPath` fields in the response.
