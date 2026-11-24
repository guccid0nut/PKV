---
id: MCP-014
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "`kongor_ui_control` Target Window 'hon_x64' Not Found on Offscreen Client"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Services/UiControlService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: ["MCP-012", "MCP-028", "MCP-042", "MCP-058", "MCP-062", "MCP-069", "MCP-074", "MCP-211"]
occurrences: 16
date: 2026-09-24
last_seen: 2026-10-27
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` resolves `K2_Class` offscreen windows via PID-based `EnumWindows` and fallback IPC."
---

# MCP-014: `kongor_ui_control` Target Window 'hon_x64' Not Found on Offscreen Client

## Summary
Tracked MCP issue on `kongor` / `kongor_ui_control` (category: `window-resolution`, status: `investigating`, occurrences: 16). Send Enter key to client window. -> Failed with `Target window 'hon_x64' not found.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Send Enter key to client window. |
| **Actual (Symptom)** | Failed with `Target window 'hon_x64' not found.` |
| **Root Cause** | In offscreen/headless mode, the Win32 window of `hon_x64.exe` is created with `MainWindowHandle = 0` / hidden offscreen at `(-32000, -32000)`, which Win32 desktop window enumeration (`FindWindow` / `GetMainWindowHandle`) cannot resolve. |
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

  Target window 'hon_x64' not found.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` resolves `K2_Class` offscreen windows via PID-based `EnumWindows` and fallback IPC.
