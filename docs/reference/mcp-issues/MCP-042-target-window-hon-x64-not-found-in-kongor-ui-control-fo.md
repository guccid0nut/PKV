---
id: MCP-042
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "Target Window 'hon_x64' Not Found in kongor_ui_control for Offscreen Process"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` enumerates `K2_Class`/`K2_Vulkan` windows and provides self-healing guidance."
---

# MCP-042: Target Window 'hon_x64' Not Found in kongor_ui_control for Offscreen Process

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Send Enter key to dismiss active in-game dialog. |
| **Actual (Symptom)** | Failed with `Target window 'hon_x64' not found.` |
| **Root Cause** | The offscreen client window (`hon_x64`) has the window class `K2_Class` and title `"Heroes of Newerth"`. When passing `target: "hon_x64"`, `FindWindowEx` or process-to-HWND lookup in `kongor_ui_control` failed if targeting string doesn't match window title or requires `*Heroes of Newerth*` / HWND. For dismissing dialogs, Lua script (`Cmd(...)` or K... |
| **Fix / Remediation** | Target via `*Heroes of Newerth*` or execute UI command through K2 Lua. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "key",
    "key": "Enter",
    "target": "hon_x64"
  }
  

Logs:

  Encountered error in tool execution: {
    "status": "error",
    "action": "key",
    "target": "hon_x64",
    "windowTitle": "",
    "hwnd": "0x00000000",
    "key": "Enter",
    "errorMessage": "Target window 'hon_x64' not found.",
    "timestamp": "2026-09-25T08:34:17.8145543+00:00"
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` enumerates `K2_Class`/`K2_Vulkan` windows and provides self-healing guidance.
