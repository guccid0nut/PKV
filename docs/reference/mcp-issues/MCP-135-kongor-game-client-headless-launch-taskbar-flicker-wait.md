---
id: MCP-135
server: kongor
tool: kongor_game_client_manage
category: window-resolution
title: "KONGOR-GAME-CLIENT-HEADLESS-LAUNCH-TASKBAR-FLICKER-WAIT-TIMEOUT"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [kongor, kongor_game_client_manage, window-resolution]
prevention_rule: "Avoid launching `hon_x64.exe` for static VFS/entity/Lua audits when all required telemetry (`console.log`, `.s2z` VFS archives, `.entity` XMLs, and Lua caches) is already available on disk; only launch the client when live visual or runtime..."
---

# MCP-135: KONGOR-GAME-CLIENT-HEADLESS-LAUNCH-TASKBAR-FLICKER-WAIT-TIMEOUT

## Summary
Tracked MCP issue on `kongor` / `kongor_game_client_manage` (category: `window-resolution`, status: `resolved`, occurrences: 1). Client launches invisibly offscreen without taskbar disruption and reaches `engine_ready`. -> Client icon repeatedly appeared and disappeared on the Windows taskbar during initialization; user closed the process, causing `kongor_wait_for` to time out after 15s (`running: false`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Client launches invisibly offscreen without taskbar disruption and reaches `engine_ready`. |
| **Actual (Symptom)** | Client icon repeatedly appeared and disappeared on the Windows taskbar during initialization; user closed the process, causing `kongor_wait_for` to time out after 15s (`running: false`). |
| **Root Cause** | Launching `hon_x64.exe` in `headless` mode while K2 creates/recreates its D3D9/Vulkan window and `WindowCapturer` / offscreen parking repositions it causes the taskbar button style (`WS_EX_APPWINDOW` / `WS_EX_TOOLWINDOW`) to toggle during startup, resulting in visible taskbar flickering. |
| **Fix / Remediation** | Avoid launching `hon_x64.exe` for static VFS/entity/Lua audits when all required telemetry (`console.log`, `.s2z` VFS archives, `.entity` XMLs, and Lua caches) is already available on disk; only launch the client when live visual or runtime verification is needed, or attach to an already-running client session. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "launch", "window_mode": "headless"}` / `{"condition": "engine_ready", "timeoutMs": 15000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Avoid launching `hon_x64.exe` for static VFS/entity/Lua audits when all required telemetry (`console.log`, `.s2z` VFS archives, `.entity` XMLs, and Lua caches) is already available on disk; only launch the client when live visual or runtime...
