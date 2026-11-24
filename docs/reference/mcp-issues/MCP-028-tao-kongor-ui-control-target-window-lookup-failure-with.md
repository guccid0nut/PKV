---
id: MCP-028
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "TAO `kongor_ui_control` Target Window Lookup Failure with Headless Offscreen Client"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` enumerates `K2_Class` offscreen windows at `(-32000, -32000)` via `WindowCapturer`."
---

# MCP-028: TAO `kongor_ui_control` Target Window Lookup Failure with Headless Offscreen Client

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | `kongor_ui_control` locates the offscreen `hon_x64` window by process ID or window enumeration and dispatches the virtual keystroke (`VK_RETURN`). |
| **Actual (Symptom)** | Returned error: `Target window 'hon_x64' not found.` (`hwnd: 0x00000000`). |
| **Root Cause** | Headless/offscreen game clients started at `(-32000, -32000)` do not report a standard top-level `MainWindowHandle` (`MainWindowHandle: 0` in Win32 Process queries). - UI control lookups relying strictly on `Process.MainWindowHandle` fail to resolve child/offscreen windows of `hon_x64.exe` without Win32 `EnumWindows` filtering by `GetWindowThreadPr... |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Services/UiControlService.cs`: Window targeting delegated to `WindowCapturer.FindWindowForProcessId(processId)` which enumerates `K2_Class` window handles even when parked offscreen at `(-32000, -32000)`. - Code patch in `src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs`: Key actions sending `Escape` fal... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "key",
    "target": "hon_x64",
    "key": "Enter"
  }
  

Logs:

  {
    "status": "error",
    "action": "key",
    "target": "hon_x64",
    "windowTitle": "",
    "hwnd": "0x00000000",
    "key": "Enter",
    "errorMessage": "Target window 'hon_x64' not found."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` enumerates `K2_Class` offscreen windows at `(-32000, -32000)` via `WindowCapturer`.
