---
id: MCP-020
server: kongor
tool: kongor_k2_ui_inspect
category: runtime-error
title: "K2 UI Inspect Live Query Error (UIManager.GetActiveInterfaceName Invalid Widget)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-09-24
last_seen: 2026-11-04
tags: [kongor, kongor_k2_ui_inspect, runtime-error]
prevention_rule: "Query valid widget paths with interface context and safe `pcall` guards."
---

# MCP-020: K2 UI Inspect Live Query Error (UIManager.GetActiveInterfaceName Invalid Widget)

## Summary
Tracked MCP issue on `kongor` / `kongor_k2_ui_inspect` (category: `runtime-error`, status: `documented`, occurrences: 2). Traverse live UI widget hierarchy from root interface. -> Returned error `[ERROR] [string "local iface=UIManager.GetActiveInterfaceNam..."]:1: invalid Widget`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_k2_ui_inspect` |
| **Expected** | Traverse live UI widget hierarchy from root interface. |
| **Actual (Symptom)** | Returned error `[ERROR] [string "local iface=UIManager.GetActiveInterfaceNam..."]:1: invalid Widget`. |
| **Root Cause** | `BuildLiveUiLuaScript` in `K2UiInspectService.cs` had two fatal flaws: 1. In K2 Lua root, `UIManager.GetActiveInterfaceName` does not exist, and `UIManager.GetInterface('main')` returns an `Interface` userdata, NOT a `Widget`. Calling widget methods (`GetAbsoluteX`, `GetTypeName`) directly on the `Interface` userdata throws `invalid Widget` from na... |
| **Fix / Remediation** | Patched `BuildLiveUiLuaScript` in `K2UiInspectService.cs` with safe `pcall` wrappers for all introspection methods and proper `Interface:GetChildren()` traversal across candidate interfaces. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "depth": 10,
    "mode": "live"
  }
  

Logs:

  [ERROR] [string "local iface=UIManager.GetActiveInterfaceNam..."]:1: invalid Widget
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_k2_ui_inspect
```

## Prevention Invariant
Query valid widget paths with interface context and safe `pcall` guards.
