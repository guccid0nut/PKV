---
id: MCP-211
server: kongor
tool: kongor_ui_control
category: window-resolution
title: "KONGOR-UI-CONTROL-CLICK-WIDGET-CHILD-INTERFACE"
status: superseded
superseded_by: "MCP-014"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_ui_control, window-resolution]
prevention_rule: "Use window-relative coordinates (`x`, `y`) with `kongor_ui_control` (`action: 'click'`) or trigger via `kongor_game_client_manage` (`action: 'command'`)."
---

# MCP-211: KONGOR-UI-CONTROL-CLICK-WIDGET-CHILD-INTERFACE

## Summary
Repeat occurrence of [`MCP-014`] on `kongor` / `kongor_ui_control` (`window-resolution`). Superseded by `MCP-014`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Click `midbar_button_matchmaking` via `kongor_ui_control`. |
| **Actual (Symptom)** | Returned `status: "fail", method: "none"` after 8082ms because `kongor_ui_control`'s widget resolver relies on `willowmaker-ipc` rather than window-relative coordinate clicks (`x`, `y`). |
| **Root Cause** | `kongor_ui_control` widget lookup queries `willowmaker-ipc`, whereas coordinate clicks (`x`, `y`) dispatch directly via Win32 `PostMessage` to the target HWND. |
| **Fix / Remediation** | Use window-relative coordinates (`x`, `y`) with `kongor_ui_control` (`action: "click"`) or trigger via `kongor_game_client_manage` (`action: "command"`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "click", "interface": "main", "target": "hon_x64", "widget": "midbar_button_matchmaking"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Use window-relative coordinates (`x`, `y`) with `kongor_ui_control` (`action: 'click'`) or trigger via `kongor_game_client_manage` (`action: 'command'`).
