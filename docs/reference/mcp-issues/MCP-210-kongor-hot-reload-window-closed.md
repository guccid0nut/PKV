---
id: MCP-210
server: kongor
tool: kongor_hot_reload
category: window-resolution
title: "KONGOR-HOT-RELOAD-WINDOW-CLOSED"
status: resolved
verified_by: "McpToolContractTests.MCP003_And_MCP088_And_MCP210_OfflineClientServerAndHotReloadRemediation"
superseded_by: ""
supersedes: ["MCP-216"]
occurrences: 3
date: 2026-10-14
last_seen: 2026-10-16
tags: [kongor, kongor_hot_reload, window-resolution]
prevention_rule: "Verify `MainWindowHandle != 0` via `Get-Process hon_x64` or `kongor_game_client_manage` (`action: 'status'`) before invoking `kongor_hot_reload`, and relaunch headless client if the window was closed."
---

# MCP-210: KONGOR-HOT-RELOAD-WINDOW-CLOSED

## Summary
Tracked MCP issue on `kongor` / `kongor_hot_reload` (category: `window-resolution`, status: `resolved`, occurrences: 2). Trigger interface hot-reload on `PID 35316`. -> Returned `Named pipe 'kongor-native-client' is not reachable` because the client window had been closed (`MainWindowHandle = 0`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_hot_reload` |
| **Expected** | Trigger interface hot-reload on `PID 35316`. |
| **Actual (Symptom)** | Returned `Named pipe 'kongor-native-client' is not reachable` because the client window had been closed (`MainWindowHandle = 0`). |
| **Root Cause** | The user closed the client window between turns (`MainWindowHandle = 0`), terminating the main window loop and named pipe listener. |
| **Fix / Remediation** | Verify `MainWindowHandle != 0` via `Get-Process hon_x64` or `kongor_game_client_manage` (`action: "status"`) before invoking `kongor_hot_reload`, and relaunch headless client if the window was closed. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "trigger"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_hot_reload
```

## Prevention Invariant
Verify `MainWindowHandle != 0` via `Get-Process hon_x64` or `kongor_game_client_manage` (`action: 'status'`) before invoking `kongor_hot_reload`, and relaunch headless client if the window was closed.
