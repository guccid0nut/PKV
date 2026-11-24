---
id: MCP-062
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "Target Window 'hon_x64' Not Found in kongor_ui_control on Headless D3D9 Client"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "Route commands via in-engine Lua (`kongor_game_lua`) or console IPC rather than virtual keystrokes."
---

# MCP-062: Target Window 'hon_x64' Not Found in kongor_ui_control on Headless D3D9 Client

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Click coordinate (640, 332) inside the running client window to dismiss the modal dialog. |
| **Actual (Symptom)** | Failed with error: `Target window 'hon_x64' not found. Make sure the application is active.` |
| **Root Cause** | The client is running headless offscreen at `(-32000, -32000)` with `vid_driver d3d9`. Window resolution logic in `UiControlService` checks for visible windows or process main window handle, which fails when the window title is empty or parked offscreen. |
| **Fix / Remediation** | Use IPC / Lua to dismiss in-game modal dialogs. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "click",
    "target": "hon_x64",
    "x": 640,
    "y": 332
  }
  

Logs:

  {
    "status": "error",
    "action": "click",
    "target": "hon_x64",
    "windowTitle": "",
    "hwnd": "0x00000000",
    "errorMessage": "Target window 'hon_x64' not found. Make sure the application is active."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Route commands via in-engine Lua (`kongor_game_lua`) or console IPC rather than virtual keystrokes.
