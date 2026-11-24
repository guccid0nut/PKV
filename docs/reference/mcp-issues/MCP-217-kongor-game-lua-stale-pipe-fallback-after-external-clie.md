---
id: MCP-217
server: kongor
tool: kongor_game_lua
category: transport-eof
title: "KONGOR-GAME-LUA-STALE-PIPE-FALLBACK-AFTER-EXTERNAL-CLIENT-RESPAWN"
status: investigating
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-15
last_seen: 2026-10-20
tags: [kongor, kongor_game_lua, transport-eof]
prevention_rule: "After respawning `hon_x64.exe` externally via PowerShell scripts, always call `kongor_game_client_manage` (`action: 'status'` or `action: 'command'`) first or send JSON-RPC directly to `\\.\pipe\kongor-native-client` so the active pipe hand..."
---

# MCP-217: KONGOR-GAME-LUA-STALE-PIPE-FALLBACK-AFTER-EXTERNAL-CLIENT-RESPAWN

## Summary
Tracked MCP issue on `kongor` / `kongor_game_lua` (category: `transport-eof`, status: `investigating`, occurrences: 2). Execute Lua script over `\\.\pipe\kongor-native-client` on the newly spawned `hon_x64.exe` process. -> Returned `{"status": "error", "channel": "in_game_console", "response": null, "executionDurationMs": 6061.47}` because `GameIpcBridge` held a broken pipe handle from the prior terminated `hon_x64.exe` PID and fell back to `in_game_console`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Execute Lua script over `\\.\pipe\kongor-native-client` on the newly spawned `hon_x64.exe` process. |
| **Actual (Symptom)** | Returned `{"status": "error", "channel": "in_game_console", "response": null, "executionDurationMs": 6061.47}` because `GameIpcBridge` held a broken pipe handle from the prior terminated `hon_x64.exe` PID and fell back to `in_game_console`. |
| **Root Cause** | After stopping `hon_x64.exe` via `kongor_game_client_manage` (`action: "stop"`) and spawning a new `hon_x64.exe` externally via `scripts/launch_onscreen_client.ps1`, calling `kongor_game_lua` directly before refreshing `kongor_game_client_manage` (`action: "status"` or `action: "command"`) encountered a broken pipe on the first write and fell back... |
| **Fix / Remediation** | After respawning `hon_x64.exe` externally via PowerShell scripts, always call `kongor_game_client_manage` (`action: "status"` or `action: "command"`) first or send JSON-RPC directly to `\\.\pipe\kongor-native-client` so the active pipe handle is re-bound before invoking `kongor_game_lua`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "Set('con_flushLog', 'true', 'bool') ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
After respawning `hon_x64.exe` externally via PowerShell scripts, always call `kongor_game_client_manage` (`action: 'status'` or `action: 'command'`) first or send JSON-RPC directly to `\\.\pipe\kongor-native-client` so the active pipe hand...
