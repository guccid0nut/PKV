---
id: MCP-106
server: kongor
tool: kongor_game_server_command
category: window-resolution
title: "`kongor` / `kongor_game_server_command` Target Port 11335 Inside Docker Linux Container Not Found Locally"
status: superseded
superseded_by: "MCP-088"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_server_command, window-resolution]
prevention_rule: "For Docker-hosted dedicated servers, send commands through COMPEL API (`http://127.0.0.1:11334`) or `docker exec compel-dedicated-server` or `kongor_compel_manage`."
---

# MCP-106: `kongor` / `kongor_game_server_command` Target Port 11335 Inside Docker Linux Container Not Found Locally

## Summary
Repeat occurrence of [`MCP-088`] on `kongor` / `kongor_game_server_command` (`window-resolution`). Superseded by `MCP-088`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Send command to dedicated server worker on port 11335. |
| **Actual (Symptom)** | Failed with `No running dedicated game server worker found on port 11335.` |
| **Root Cause** | `kongor_game_server_command` inspects local Windows named pipes (`\\.\pipe\hon-server-11335`) and Windows processes. Port 11335 is hosted inside the Linux Docker container `compel-dedicated-server`. |
| **Fix / Remediation** | Route dedicated server commands targeting Docker to `kongor_compel_manage` or `docker exec`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "port": 11335,
    "command": "ServerStatus"
  }
  

Logs:

  "status": "target_offline", "message": "No running dedicated game server worker found on port 11335."
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
For Docker-hosted dedicated servers, send commands through COMPEL API (`http://127.0.0.1:11334`) or `docker exec compel-dedicated-server` or `kongor_compel_manage`.
