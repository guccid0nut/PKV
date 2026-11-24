---
id: MCP-213
server: kongor
tool: kongor_ui_flow
category: window-resolution
title: "KONGOR-GAME-IPC-BRIDGE-STICKY-WILLOWMAKER-IPC-FALLBACK-DURING-CLIENT-BOOT"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-10-14
last_seen: 2026-11-06
tags: [kongor, kongor_ui_flow, window-resolution]
prevention_rule: "Patched `GameIpcBridge.SendRequestAsync` and `ExecuteCommandAsync` in `src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs` to automatically call `CleanupExistingConnection()` when `willowmaker-ipc` times out and immediately upgrade to `kong..."
---

# MCP-213: KONGOR-GAME-IPC-BRIDGE-STICKY-WILLOWMAKER-IPC-FALLBACK-DURING-CLIENT-BOOT

## Summary
Tracked MCP issue on `kongor` / `kongor_ui_flow` (category: `window-resolution`, status: `investigating`, occurrences: 3). Route Lua commands over `\\.\pipe\kongor-native-client` (`version.dll`). -> Timed out after 3000ms on `willowmaker-ipc` while `kongor_game_client_manage` (`action: "command"`) succeeded in 9.08ms over `kongor-native-client`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_flow` |
| **Expected** | Route Lua commands over `\\.\pipe\kongor-native-client` (`version.dll`). |
| **Actual (Symptom)** | Timed out after 3000ms on `willowmaker-ipc` while `kongor_game_client_manage` (`action: "command"`) succeeded in 9.08ms over `kongor-native-client`. |
| **Root Cause** | When `GameIpcBridge.ConnectAsync` was called while `hon_x64.exe` was still cold-booting (`MainWindowHandle == 0`), `kongor-native-client` was not yet listening, so the singleton `GameIpcBridge` fell back to `willowmaker-ipc` (hosted by the desktop `WILLOWMAKER.exe` launcher) and remained connected (`IsConnected == true`) even after `SendRequestAsyn... |
| **Fix / Remediation** | Patched `GameIpcBridge.SendRequestAsync` and `ExecuteCommandAsync` in `src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs` to automatically call `CleanupExistingConnection()` when `willowmaker-ipc` times out and immediately upgrade to `kongor-native-client`. When using a long-lived pre-patch stdio MCP process, wait until `kongor_game_client_manage`... |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "bypass_login", "accountName": "guccidonut"}` and `{"action": "eval", "code": "return tostring(GetCvarString('host_version'))"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_flow
```

## Prevention Invariant
Patched `GameIpcBridge.SendRequestAsync` and `ExecuteCommandAsync` in `src/tools/kongor-cli/Mcp/Services/GameIpcBridge.cs` to automatically call `CleanupExistingConnection()` when `willowmaker-ipc` times out and immediately upgrade to `kong...
