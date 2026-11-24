---
id: MCP-281
server: kongor
tool: kongor_k2_lint
category: false-diagnostic
title: "K2 linter treats legacy event scripts as Lua"
status: resolved
verified_by: "f3c7add47 + McpToolContractTests.Tranche2 + live NetGuard stdio audit (main.interface 0 errors)"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_k2_lint, false-diagnostic]
prevention_rule: "Distinguish legacy K2 event attributes from onloadlua and other Lua callbacks before applying Lua syntax rules."
---

# MCP-281: K2 linter treats legacy event scripts as Lua

## Summary
Linting the Store2 package via the TAO bridge returned three Lua syntax errors and 68 warnings; two errors were traced to legacy K2 event attributes, while the third needs separate verification. The CLI exited zero and the outcome log recorded verdict=pass and firstCallSucceeded=true despite the nested lint status=error. No source fix has been applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_k2_lint` |
| **Expected** | Apply Lua grammar to Lua callbacks and legacy K2 grammar to legacy callbacks. |
| **Actual (Symptom)** | Reports an unexpected Lua end at element line 530 and two invalid Lua operator errors at element line 946; the latter are in legacy onevent attributes at lines 951 and 1001. |
| **Root Cause** | K2LintService includes legacy event names in LuaEventAttributes and sends their values to LintLuaScript. The unexpected end finding also needs separate verification. |
| **Fix / Remediation** | Separate attribute grammars and regression-test both. Decide whether operation-level failures must affect outcome telemetry and CLI exit status; meanwhile inspect each diagnostic in context and check report status rather than only the CLI exit code or outer outcome verdict. |

## Invocation Parameters & Raw Logs
```text
kongor_call_tool action=call tool_name=kongor_k2_lint
arguments_json: path=games/client/override/ui/fe2/store2.package
status=error; errors=3; warnings=68; process exit=0
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_k2_lint
```

## Prevention Invariant
Distinguish legacy K2 event attributes from onloadlua and other Lua callbacks before applying Lua syntax rules.
