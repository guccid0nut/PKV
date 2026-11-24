---
id: MCP-096
server: kongor
tool: kongor_ui_control
category: parameter-mismatch
title: "`kongor` / `kongor_ui_control` Parameter Validation: Action 'key_press' Rejected"
status: superseded
superseded_by: "MCP-065"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_ui_control, parameter-mismatch]
prevention_rule: "Always use canonical `action: 'key'` (or `action: 'press'`) when invoking `kongor_ui_control`."
---

# MCP-096: `kongor` / `kongor_ui_control` Parameter Validation: Action 'key_press' Rejected

## Summary
Repeat occurrence of [`MCP-065`] on `kongor` / `kongor_ui_control` (`parameter-mismatch`). Superseded by `MCP-065`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Send Return keypress to offscreen client window. |
| **Actual (Symptom)** | Returned error: `Unknown action 'key_press'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'`. |
| **Root Cause** | Canonical action for key strokes in `kongor_ui_control` is `action: "key"` (or aliases `key_tap`, `press`). `key_press` was not in the accepted action list. |
| **Fix / Remediation** | Use `action: "key"` with `key: "Return"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "key_press",
    "key": "Return",
    "target": "hon_x64"
  }
  

Logs:

  Unknown action 'key_press'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'. (Supported aliases: 'key_tap', 'press', 'mouse_click', 'write', 'status')
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Always use canonical `action: 'key'` (or `action: 'press'`) when invoking `kongor_ui_control`.
