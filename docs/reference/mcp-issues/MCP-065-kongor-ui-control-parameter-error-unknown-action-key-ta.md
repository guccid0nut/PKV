---
id: MCP-065
server: kongor
tool: kongor_ui_control
category: parameter-mismatch
title: "kongor_ui_control Parameter Error: Unknown Action 'key_tap' (Expected 'key')"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: ["MCP-096", "MCP-100"]
occurrences: 4
date: 2026-09-25
last_seen: 2026-11-01
tags: [kongor, kongor_ui_control, parameter-mismatch]
prevention_rule: "`UiControlHandler` transparently aliases `key_tap`, `tap`, `press`, `keypress`, `mouse_click`, and `write`."
---

# MCP-065: kongor_ui_control Parameter Error: Unknown Action 'key_tap' (Expected 'key')

## Summary
Tracked MCP issue on `kongor` / `kongor_ui_control` (category: `parameter-mismatch`, status: `investigating`, occurrences: 4). Send an Enter keystroke to the focused game window. -> Failed with schema/action error: `Unknown action 'key_tap'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Send an Enter keystroke to the focused game window. |
| **Actual (Symptom)** | Failed with schema/action error: `Unknown action 'key_tap'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'.` |
| **Root Cause** | `UiControlHandler.HandleAsync` previously executed a strict C# switch statement on `action` without recognizing common automation synonyms (`key_tap`, `tap`, `press`, `keypress`, `mouse_click`, `write`). |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs`: Expanded action switch expressions to transparently alias: - `"key_tap"`, `"tap"`, `"press"`, `"keypress"`, `"send_key"`, `"sendkey"` $\to$ `HandleKeyAsync` - `"mouse_click"`, `"press_button"`, `"button_click"`, `"click_element"` $\to$ `HandleClickAsync` - `"write"`, `"input"`,... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "key_tap",
    "key": "Enter"
  }
  

Logs:

  Unknown action: key_tap. Supported actions: click, move, drag, key, type, scroll, dismiss_console.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
`UiControlHandler` transparently aliases `key_tap`, `tap`, `press`, `keypress`, `mouse_click`, and `write`.
