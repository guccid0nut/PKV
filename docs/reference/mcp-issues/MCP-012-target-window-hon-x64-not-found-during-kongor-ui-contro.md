---
id: MCP-012
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "Target Window 'hon_x64' Not Found During kongor_ui_control"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "Verify process/window existence via `Get-Process hon_x64` before dispatching window clicks."
---

# MCP-012: Target Window 'hon_x64' Not Found During kongor_ui_control

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Click coordinate (640, 333) in the client window. |
| **Actual (Symptom)** | Failed with `Target window 'hon_x64' not found. Make sure the application is active.` |
| **Root Cause** | The `hon_x64.exe` process was terminated right before the command executed due to host AppHost stream termination. |
| **Fix / Remediation** | Pre-flight process checks before calling `kongor_ui_control`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "click",
    "target": "hon_x64",
    "x": 640,
    "y": 333
  }
  

Logs:

  Target window 'hon_x64' not found. Make sure the application is active.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Verify process/window existence via `Get-Process hon_x64` before dispatching window clicks.
