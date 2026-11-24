---
id: MCP-071
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "`kongor` kongor_game_lua Script Error: 'GetNumChildren' Nil Method Call"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "`ClientCommandRemediationService` diagnoses nil widget dereferences with actionable recovery hints."
---

# MCP-071: `kongor` kongor_game_lua Script Error: 'GetNumChildren' Nil Method Call

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Traverse K2 UI widget tree using hypothetical `GetNumChildren` method. |
| **Actual (Symptom)** | Failed with Lua error: `attempt to call method 'GetNumChildren' (a nil value)`. |
| **Root Cause** | In K2 Lua widget API, `CWidget` does not expose `GetNumChildren()` or `GetChild()`. Instead, use `kongor_k2_ui_inspect` or access named widgets directly via `iface:GetWidget('widget_name')`. |
| **Fix / Remediation** | Pivot to `kongor_k2_ui_inspect` or direct named widget lookups. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "code": "local iface = UIManager.GetInterface('loading'); ... w:GetNumChildren() ..."
  }
  

Logs:

  [ERROR] [string "..."]:1: attempt to call method 'GetNumChildren' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`ClientCommandRemediationService` diagnoses nil widget dereferences with actionable recovery hints.
