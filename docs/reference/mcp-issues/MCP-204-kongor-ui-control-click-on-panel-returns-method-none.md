---
id: MCP-204
server: kongor
tool: kongor_ui_control
category: runtime-error
title: "KONGOR-UI-CONTROL-CLICK-ON-PANEL-RETURNS-METHOD-NONE"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-14
last_seen: 2026-10-16
tags: [kongor, kongor_ui_control, runtime-error]
prevention_rule: "Wrap interactive click zones in a transparent `<button color='invisible' noclick='0'>` or bind a `watch1` trigger (`TriggerGDEasterEgg`) alongside `onclicklua` so both physical mouse clicks and programmatic UI triggers execute identically."
---

# MCP-204: KONGOR-UI-CONTROL-CLICK-ON-PANEL-RETURNS-METHOD-NONE

## Summary
Tracked MCP issue on `kongor` / `kongor_ui_control` (category: `runtime-error`, status: `resolved`, occurrences: 1). Trigger `onclicklua` on `<panel name="prelogin_version_label_panel">`. -> Returned `{"action": "click", "method": "none", "success": true, "target": "prelogin_version_label_panel"}` without invoking the panel's `onclicklua`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Trigger `onclicklua` on `<panel name="prelogin_version_label_panel">`. |
| **Actual (Symptom)** | Returned `{"action": "click", "method": "none", "success": true, "target": "prelogin_version_label_panel"}` without invoking the panel's `onclicklua`. |
| **Root Cause** | `kongor_ui_control` (`action: "click"`) resolves `<button>` widgets or registered button triggers. Targeting a generic `<panel>` without a child `<button>` or trigger watcher results in `method: "none"`. |
| **Fix / Remediation** | Wrap interactive click zones in a transparent `<button color="invisible" noclick="0">` or bind a `watch1` trigger (`TriggerGDEasterEgg`) alongside `onclicklua` so both physical mouse clicks and programmatic UI triggers execute identically. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "click", "target": "prelogin_version_label_panel"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Wrap interactive click zones in a transparent `<button color='invisible' noclick='0'>` or bind a `watch1` trigger (`TriggerGDEasterEgg`) alongside `onclicklua` so both physical mouse clicks and programmatic UI triggers execute identically.
