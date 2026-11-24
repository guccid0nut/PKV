---
id: MCP-107
server: kongor
tool: kongor_game_command
category: window-resolution
title: "`kongor` / `kongor_game_command` Target `server:11335` Docker Dedicated Server Local Process Lookup"
status: superseded
superseded_by: "MCP-003"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_command, window-resolution]
prevention_rule: "When targeting Docker-hosted dedicated servers on port 11335, use `docker exec compel-dedicated-server` or the COMPEL control API (`http://127.0.0.1:11334`) or client-side game orchestration commands."
---

# MCP-107: `kongor` / `kongor_game_command` Target `server:11335` Docker Dedicated Server Local Process Lookup

## Summary
Repeat occurrence of [`MCP-003`] on `kongor` / `kongor_game_command` (`window-resolution`). Superseded by `MCP-003`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Send command to dedicated server worker on port 11335. |
| **Actual (Symptom)** | Failed with `target_offline`: `No running dedicated game server worker found on port 11335.` |
| **Root Cause** | `kongor_game_command` targets local Windows dedicated server workers by looking for Windows processes/named pipes. Dedicated server port 11335 is running inside the Linux Docker container `compel-dedicated-server`. |
| **Fix / Remediation** | Routed Docker container commands via `docker exec` and client RPC. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "target": "server",
    "port": 11335,
    "command": "Status"
  }
  

Logs:

  {
    "status": "target_offline",
    "target": "server:11335",
    "message": "No running dedicated game server worker found on port 11335."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
When targeting Docker-hosted dedicated servers on port 11335, use `docker exec compel-dedicated-server` or the COMPEL control API (`http://127.0.0.1:11334`) or client-side game orchestration commands.
