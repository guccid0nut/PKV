---
id: MCP-222
server: kongor
tool: kongor_ui_control
category: ipc-timeout
title: "KONGOR-UI-CONTROL-DISPATCH-WILLOWMAKER-IPC-UNAVAILABLE"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-15
last_seen: 2026-10-25
tags: [kongor, kongor_ui_control, ipc-timeout]
prevention_rule: "When `WILLOWMAKER.exe` is open or `hon_x64.exe` is listening on `\\.\pipe\kongor-native-client`, use `kongor_game_client_manage` (`action: 'command'`, `command: 'Script ...'`) or `action: 'click'` (coordinate/Win32 click) rather than `kongo..."
---

# MCP-222: KONGOR-UI-CONTROL-DISPATCH-WILLOWMAKER-IPC-UNAVAILABLE

## Summary
Tracked MCP issue on `kongor` / `kongor_ui_control` (category: `ipc-timeout`, status: `documented`, occurrences: 2). Dispatch DOM `click` event to `midbar_button_play` in `hon_x64.exe` (`PID 87760`). -> Returned `Failed to dispatch UI event to widget 'midbar_button_play'. IPC connection to game client is not available.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Dispatch DOM `click` event to `midbar_button_play` in `hon_x64.exe` (`PID 87760`). |
| **Actual (Symptom)** | Returned `Failed to dispatch UI event to widget 'midbar_button_play'. IPC connection to game client is not available.` |
| **Root Cause** | `kongor_ui_control` (`action: "dispatch"`) uses `GameIpcBridge` (`willowmaker-ipc`), whereas `hon_x64.exe` listens on `\\.\pipe\kongor-native-client` (`kongor_game_client_manage action: "command"`). |
| **Fix / Remediation** | When `WILLOWMAKER.exe` is open or `hon_x64.exe` is listening on `\\.\pipe\kongor-native-client`, use `kongor_game_client_manage` (`action: "command"`, `command: "Script ..."`) or `action: "click"` (coordinate/Win32 click) rather than `kongor_ui_control` `action: "dispatch"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "dispatch", "event": "click", "target": "hon_x64", "widget": "midbar_button_play"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
When `WILLOWMAKER.exe` is open or `hon_x64.exe` is listening on `\\.\pipe\kongor-native-client`, use `kongor_game_client_manage` (`action: 'command'`, `command: 'Script ...'`) or `action: 'click'` (coordinate/Win32 click) rather than `kongo...
