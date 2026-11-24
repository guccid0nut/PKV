---
id: MCP-069
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "`kongor` kongor_ui_control Target Window 'pid:55592' Not Found"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` auto-migrates stale/offline client PIDs to the single active client and defaults empty target to `hon_x64`."
---

# MCP-069: `kongor` kongor_ui_control Target Window 'pid:55592' Not Found

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Click coordinate (960, 335) in offscreen game window PID 55592. |
| **Actual (Symptom)** | Failed with `Target window 'pid:55592' not found. Make sure the application is active.` |
| **Root Cause** | `kongor_ui_control`'s target resolver looks for process names (`hon_x64`), window title patterns (`*Heroes of Newerth*`), or hex HWNDs (`0x...`), but does not parse the `pid:12345` prefix format that `kongor_capture_window` supports. |
| **Fix / Remediation** | Resolving client HWND directly or using `target: "*Heroes of Newerth*"` / K2 Lua IPC. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "click",
    "target": "pid:55592",
    "x": 960,
    "y": 335
  }
  

Logs:

  Target window 'pid:55592' not found. Make sure the application is active.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` auto-migrates stale/offline client PIDs to the single active client and defaults empty target to `hon_x64`.
