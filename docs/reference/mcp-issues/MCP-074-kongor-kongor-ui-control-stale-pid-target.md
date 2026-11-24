---
id: MCP-074
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "`kongor` kongor_ui_control Stale PID Target"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "`UiControlService` auto-migrates stale/dead client PIDs to the single active `hon_x64` client process."
---

# MCP-074: `kongor` kongor_ui_control Stale PID Target

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Console dismissed on target client process. |
| **Actual (Symptom)** | Failed with `Target game window 'pid:25452' not found. Ensure the game client is running.` |
| **Root Cause** | The client had restarted with a new PID (34476), rendering PID 25452 obsolete. |
| **Fix / Remediation** | Updated target PID to active client PID 34476. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "dismiss_console",
    "target": "pid:25452"
  }
  

Logs:

  {
    "status": "error",
    "action": "dismiss_console",
    "target": "pid:25452",
    "errorMessage": "Target game window 'pid:25452' not found. Ensure the game client is running."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlService` auto-migrates stale/dead client PIDs to the single active `hon_x64` client process.
