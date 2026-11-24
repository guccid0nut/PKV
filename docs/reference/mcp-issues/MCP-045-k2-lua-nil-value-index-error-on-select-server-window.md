---
id: MCP-045
server: kongor
tool: kongor_game_lua
category: window-resolution
title: "K2 Lua Nil Value Index Error on select_server_window"
status: superseded
superseded_by: "MCP-051"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, window-resolution]
prevention_rule: "Guard widget lookups with `if w then ... end` or `pcall`."
---

# MCP-045: K2 Lua Nil Value Index Error on select_server_window

## Summary
Repeat occurrence of [`MCP-051`] on `kongor` / `kongor_game_lua` (`window-resolution`). Superseded by `MCP-051`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Dismiss active dialogs and select_server_window. |
| **Actual (Symptom)** | Failed with `attempt to index a nil value` because `select_server_window` widget was not found on `main` interface. |
| **Root Cause** | Direct chained method calls (`:SetVisible(false)`) without checking if `GetWidget(...)` is non-nil trigger Lua runtime errors when the widget name or interface doesn't match. |
| **Fix / Remediation** | Wrap widget actions with existence guards. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "Cmd(\"CloseDialog\"); if generic_dialog_box then generic_dialog_box:SetVisible(false) end; if generic_dialog_1 then generic_dialog_1:SetVisible(false) end; UIManager.GetInterface('main'):GetWidget('select_server_window'):SetVisible(false)"
  }
  

Logs:

  [ERROR] [string "Cmd(\"CloseDialog\"); if generic_dialog_box t..."]:1: attempt to index a nil value
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Guard widget lookups with `if w then ... end` or `pcall`.
