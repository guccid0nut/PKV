---
id: MCP-004
server: kongor
tool: kongor_game_server_command
category: client-offline
title: "Dedicated Game Server Worker Offline on Target Port"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_game_server_command, client-offline]
prevention_rule: "Target specific port and pass `autoLaunch: true` or launch via `kongor_game_server_manage`."
---

# MCP-004: Dedicated Game Server Worker Offline on Target Port

## Summary
Tracked MCP issue on `kongor` / `kongor_game_server_command` (category: `client-offline`, status: `resolved`, occurrences: 1). Echo output from dedicated game server listening on port 11255. -> Failed with error: `No running dedicated game server worker found on port 11255. status: target_offline`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Echo output from dedicated game server listening on port 11255. |
| **Actual (Symptom)** | Failed with error: `No running dedicated game server worker found on port 11255. status: target_offline`. |
| **Root Cause** | Dedicated server commands target specific port instances. Port 11255 was not active in the local server pool or K3s Agones cluster. |
| **Fix / Remediation** | Governed under *Dedicated Game Server Command Routing Rule* in `GEMINI.md`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "command",
    "target": "server:11255",
    "command": "Echo HelloServer"
  }
  

Logs:

  {
    "status": "target_offline",
    "executionMode": "offline_simulation",
    "port": 11255,
    "message": "No running dedicated game server worker found on port 11255.",
    "remediation": {
      "hint": "Dedicated game server on port 11255 is offline. Pass 'autoLaunch: true' or launch server on port 11255.",
      "suggestedAction": "kongor_game_server_manage"
    }
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
Target specific port and pass `autoLaunch: true` or launch via `kongor_game_server_manage`.
