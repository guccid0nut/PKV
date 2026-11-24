---
id: MCP-079
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "`kongor` kongor_game_lua Call to Non-Existent Method `GetRootWidget` on `CInterface`"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "`ClientCommandRemediationService` auto-rewrites `GetRootWidget()` to `GetWidget('main')`."
---

# MCP-079: `kongor` kongor_game_lua Call to Non-Existent Method `GetRootWidget` on `CInterface`

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Retrieve root widget from K2 UI interface. |
| **Actual (Symptom)** | Returned `[ERROR] [string "..."]:18: attempt to call method 'GetRootWidget' (a nil value)`. |
| **Root Cause** | `CInterface` in K2 Lua does not expose `GetRootWidget()`. It exposes `GetWidget(name)` directly. |
| **Fix / Remediation** | Query specific named widgets via `main:GetWidget(name)`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "search(main:GetRootWidget(), 0)",
    "target": "client"
  }
  

Logs:

  [ERROR] [string "local main = UIManager.GetInterface('main')..."]:18: attempt to call method 'GetRootWidget' (a nil value)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`ClientCommandRemediationService` auto-rewrites `GetRootWidget()` to `GetWidget('main')`.
