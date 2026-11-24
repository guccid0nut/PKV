---
id: MCP-088
server: kongor
tool: kongor_game_server_command
category: window-resolution
title: "`kongor_game_server_command` Dedicated Server Offline on Port 11336 (Targeting Docker Worker via Windows Named Pipe)"
status: resolved
verified_by: "McpToolContractTests.MCP003_And_MCP088_And_MCP210_OfflineClientServerAndHotReloadRemediation"
superseded_by: ""
supersedes: ["MCP-097", "MCP-106", "MCP-116"]
occurrences: 4
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_game_server_command, window-resolution]
prevention_rule: "When targeting Docker-hosted dedicated servers (ports 11335..11336), dispatch commands via `docker exec compel-dedicated-server ...` or COMPEL IPC rather than host Win32 named pipes."
---

# MCP-088: `kongor_game_server_command` Dedicated Server Offline on Port 11336 (Targeting Docker Worker via Windows Named Pipe)

## Summary
Tracked MCP issue on `kongor` / `kongor_game_server_command` (category: `window-resolution`, status: `resolved`, occurrences: 4). Executing server command on dedicated worker 11336. -> Failed with `No running dedicated game server worker found on port 11336`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Executing server command on dedicated worker 11336. |
| **Actual (Symptom)** | Failed with `No running dedicated game server worker found on port 11336`. |
| **Root Cause** | Worker 11336 is running inside the Linux Docker container `compel-dedicated-server`, where commands are routed via container stdin/socat rather than the Windows Win32 named pipe `\\.\pipe\hon-server-11336` on the Windows host. |
| **Fix / Remediation** | Interacted with Docker dedicated server directly via container exec / stdin. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "port": 11336,
    "command": "Get net_enableWatermark"
  }
  

Logs:

  status: target_offline
  message: No running dedicated game server worker found on port 11336.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
When targeting Docker-hosted dedicated servers (ports 11335..11336), dispatch commands via `docker exec compel-dedicated-server ...` or COMPEL IPC rather than host Win32 named pipes.
