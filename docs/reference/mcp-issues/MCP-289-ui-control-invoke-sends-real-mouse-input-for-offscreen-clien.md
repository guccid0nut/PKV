---
id: MCP-289
server: kongor
tool: kongor_ui_control
category: safety
title: "ui_control invoke sends real mouse input for offscreen client"
status: investigating
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-17
last_seen: 2026-11-18
tags: [kongor, kongor_ui_control, safety]
prevention_rule: "Never synthesize OS-level mouse or keyboard input for an offscreen or headless target; route to in-engine widget dispatch or fail with an actionable error."
---

# MCP-289: ui_control invoke sends real mouse input for offscreen client

## Summary
`kongor_ui_control` with `action: invoke` on the offscreen client performed an OS-level mouse click at the window's off-screen coordinates and reported success. The operating system clamps such coordinates to the desktop, so the click landed on the user's primary display. Found in the 2026-11-17 live audit. Open; no fix applied. `click`, `drag`, `type` and `key` use the same input path and were not executed.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | For an offscreen or headless client the tool dispatches inside the engine (as `click_widget` and `dispatch_event` do) or refuses with an actionable error. It never moves the pointer or sends input to the desktop. |
| **Actual (Symptom)** | Response: `status: success`, `actionTaken: mouse_click`, `screenX: -9360`, `screenY: -9640`. Afterwards the pointer was at (0, 0) of the primary display. The schema default for `target` is `foreground`, so a call without `target` acts on whichever window the user has focused. |
| **Root Cause** | `UiControlService.cs` implements click, drag, type and key with foreground activation and synthesized input (`BringWindowToForeground`, `keybd_event`; around lines 557, 621, 693, 784, 871). The `invoke` response reports `actionTaken: mouse_click`, so it took that input path; the branch that selects it was not traced. The call was not blocked although the target window was parked off-screen. |
| **Fix / Remediation** | Not fixed. Until fixed, use only `click_widget`, `dispatch_event`, `dispatch` and `inspect` with an explicit `target: hon_x64`. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
row 113 kongor_ui_control {action: invoke, target: hon_x64, widget: systembar}
  -> status success, actionTaken mouse_click, screenX -9360, screenY -9640
row 110 kongor_ui_control {action: trigger_widget} -> "Unknown action 'trigger_widget'. Valid actions are: inspect, click, invoke, type, key, console, dismiss_console"
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Never synthesize OS-level mouse or keyboard input for an offscreen or headless target; route to in-engine widget dispatch or fail with an actionable error.
