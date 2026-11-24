---
id: MCP-162
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "(formerly MCP-099): KONGOR-CLIENT-EVAL-LUA-DOUBLE-QUOTE-ESCAPING"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) `KongorSharedMem.GetPendingEval()`, bypassing `CConsole::Execute` string lexing entirely."
---

# MCP-162: (formerly MCP-099): KONGOR-CLIENT-EVAL-LUA-DOUBLE-QUOTE-ESCAPING

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Successfully evaluates Lua string returning the greeting. |
| **Actual (Symptom)** | Engine console error: `[string ...]:1: ')' expected near 'Hello'`. |
| **Root Cause** | `kongor_client_eval_lua` wraps the command in `Script "local _ok, _res = pcall(loadstring(\"<CODE>\")) ..."`. When `<CODE>` contains escaped double quotes (`\"`), the K2 engine console command lexer unescapes the quotes, prematurely terminating the outer string literal. |
| **Fix / Remediation** | Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) `KongorSharedMem.GetPendingEval()`, bypassing `CConsole::Execute` string lexing entirely. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "eval", "code": "return \"Hello from Kongor Lua\""}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Resolved in Step 6 via `EvalLuaSync` (`0x000E` -> `0x000F`) `KongorSharedMem.GetPendingEval()`, bypassing `CConsole::Execute` string lexing entirely.
