---
id: MCP-097
server: kongor
tool: kongor_game_server_command
category: window-resolution
title: "`kongor` / `kongor_game_server_command` Target Offline for Docker-hosted Dedicated Server Port 11336"
status: superseded
superseded_by: "MCP-088"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_server_command, window-resolution]
prevention_rule: "For Docker-hosted dedicated servers (e.g. port 11335, 11336), route console commands via Docker stdin or COMPEL supervisor rather than `kongor_game_server_command`."
---

# MCP-097: `kongor` / `kongor_game_server_command` Target Offline for Docker-hosted Dedicated Server Port 11336

## Summary
Repeat occurrence of [`MCP-088`] on `kongor` / `kongor_game_server_command` (`window-resolution`). Superseded by `MCP-088`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Route console command to dedicated server worker on port 11336. |
| **Actual (Symptom)** | Returned error: `No running dedicated game server worker found on port 11336.` with status `target_offline`. |
| **Root Cause** | `kongor_game_server_command` resolves Windows dedicated server processes supervised locally by Willowmaker. Worker on port 11336 is executing inside Docker container `compel-dedicated-server`. |
| **Fix / Remediation** | Route command through Docker execution to worker PID 8596 stdin. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "port": 11336,
    "command": "Set svr_login guccihost_local:2002; Set svr_password cIV$c3sGUkxR!OUM; Login"
  }
  

Logs:

  {
    "status": "target_offline",
    "target": "server:11336",
    "port": 11336,
    "message": "No running dedicated game server worker found on port 11336."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
For Docker-hosted dedicated servers (e.g. port 11335, 11336), route console commands via Docker stdin or COMPEL supervisor rather than `kongor_game_server_command`.
