---
id: MCP-100
server: kongor
tool: kongor_ui_control
category: parameter-mismatch
title: "`kongor` / `kongor_ui_control` Action 'press_key' Not Recognized"
status: superseded
superseded_by: "MCP-065"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_ui_control, parameter-mismatch]
prevention_rule: "Use `action: 'key'` or `action: 'key_tap'` when sending keystrokes via `kongor_ui_control`."
---

# MCP-100: `kongor` / `kongor_ui_control` Action 'press_key' Not Recognized

## Summary
Repeat occurrence of [`MCP-065`] on `kongor` / `kongor_ui_control` (`parameter-mismatch`). Superseded by `MCP-065`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Send Escape keystroke to `hon_x64` window. |
| **Actual (Symptom)** | Failed with `Unknown action 'press_key'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'. (Supported aliases: 'key_tap', 'press', 'mouse_click', 'write', 'status')`. |
| **Root Cause** | Action name for keystrokes in `kongor_ui_control` is `key` (or supported aliases `key_tap`, `press`), not `press_key`. |
| **Fix / Remediation** | Call `kongor_ui_control` with `action: "key"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "press_key",
    "key": "Escape",
    "target": "hon_x64"
  }
  

Logs:

  {
    "status": "error",
    "action": "press_key",
    "target": "hon_x64",
    "errorMessage": "Unknown action 'press_key'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Use `action: 'key'` or `action: 'key_tap'` when sending keystrokes via `kongor_ui_control`.
