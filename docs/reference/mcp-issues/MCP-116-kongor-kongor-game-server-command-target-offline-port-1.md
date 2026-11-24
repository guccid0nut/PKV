---
id: MCP-116
server: kongor
tool: kongor_game_server_command
category: window-resolution
title: "`kongor` / `kongor_game_server_command` Target Offline: Port 11335 Running in Docker Container"
status: superseded
superseded_by: "MCP-088"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_server_command, window-resolution]
prevention_rule: "When communicating with dedicated servers running in Docker containers, interact via Docker / COMPEL container APIs or inspect logs via Docker commands rather than host-only Windows named pipe tools."
---

# MCP-116: `kongor` / `kongor_game_server_command` Target Offline: Port 11335 Running in Docker Container

## Summary
Repeat occurrence of [`MCP-088`] on `kongor` / `kongor_game_server_command` (`window-resolution`). Superseded by `MCP-088`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Retrieve server status from dedicated server worker on port 11335. |
| **Actual (Symptom)** | Failed with `No running dedicated game server worker found on port 11335`. |
| **Root Cause** | `kongor_game_server_command` routes to local host Windows processes using host named pipes (`\\.\pipe\hon-server-{port}`). Dedicated server worker #D1 on port 11335 is running inside the Linux Docker container `compel-dedicated-server`. |
| **Fix / Remediation** | Identified worker #D1 is inside Docker container `compel-dedicated-server`; route diagnostic inquiries accordingly. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "command": "ServerStatus",
    "port": 11335
  }
  

Logs:

  {
    "status": "target_offline",
    "message": "No running dedicated game server worker found on port 11335."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
When communicating with dedicated servers running in Docker containers, interact via Docker / COMPEL container APIs or inspect logs via Docker commands rather than host-only Windows named pipe tools.
