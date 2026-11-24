---
id: MCP-058
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "Target Window 'hon_x64' Not Found in kongor_ui_control on Headless Offscreen Client"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` searches `K2_Class`/`K2_Vulkan`, filters dedicated servers, and provides self-healing errors."
---

# MCP-058: Target Window 'hon_x64' Not Found in kongor_ui_control on Headless Offscreen Client

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Dispatch `Enter` virtual keystroke to the game client window to dismiss the active modal dialog. |
| **Actual (Symptom)** | Failed with error: `Target window 'hon_x64' not found.`. |
| **Root Cause** | The game client was running headless offscreen at `(-32000, -32000)` with window title `Heroes of Newerth` and process image name `hon_x64.exe` (or had already exited). `UiControlService` only checked `K2_Direct3D`, missing `K2_Class` and `K2_Vulkan`, and returned a generic "not found" message rather than diagnosing whether the process was offline... |
| **Fix / Remediation** | `UiControlService.ResolveWindowHandle` now searches all K2 window classes (`K2_Class`, `K2_Vulkan`, `K2_Direct3D`), disambiguates client processes from dedicated servers using `ClientProcessSupervisor`, and returns actionable diagnostic guidance (`BuildWindowNotFoundErrorMessage`) when offline. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "key",
    "key": "Enter",
    "target": "hon_x64"
  }
  

Logs:

  {
    "status": "error",
    "action": "key",
    "target": "hon_x64",
    "windowTitle": "",
    "hwnd": "0x00000000",
    "key": "Enter",
    "errorMessage": "Target window 'hon_x64' not found.",
    "timestamp": "2026-09-25T15:40:49.3713698+00:00"
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` searches `K2_Class`/`K2_Vulkan`, filters dedicated servers, and provides self-healing errors.
