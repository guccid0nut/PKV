---
id: MCP-243
server: kongor
tool: kongor_client_eval_lua
category: lua-eval
title: "KONGOR-CLIENT-EVAL-LUA-TESTING-GETENTITYSTATUS-RETURNS-SCALAR-TUPLE"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: []
occurrences: 10
date: 2026-10-25
last_seen: 2026-11-06
tags: [kongor, kongor_client_eval_lua, lua-eval]
prevention_rule: "Treat Testing.GetEntityStatus(entityIdx) as an integer status return (1=Active, 2=Corpse, 0=Invalid) and inspect Testing.GetEntityInfo(entityIdx) for structured fields rather than indexing Testing.GetEntityStatus as a Lua table."
---

# MCP-243: KONGOR-CLIENT-EVAL-LUA-TESTING-GETENTITYSTATUS-RETURNS-SCALAR-TUPLE

## Summary
Calling `Testing.GetEntityStatus(entityIdx)` inside `kongor_client_eval_lua` and indexing the return value as a table (`stBefore.health`) throws `attempt to index local 'stBefore' (a number value)` because `Testing.GetEntityStatus` returns the numeric K2 `EEntityStatus` enum (`1` = Active, `2` = Corpse, `0` = Dormant/Invalid) rather than a table.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Query target entity health/liveness before casting `Ability_Engineer3`. |
| **Actual (Symptom)** | `[ERROR] [string "..."]:22: attempt to index local 'stBefore' (a number value)`. |
| **Root Cause** | K2's C++ `Testing.GetEntityStatus(index)` pushes an integer status code (`lua_pushnumber`), not a Lua table. |
| **Fix / Remediation** | Compare `Testing.GetEntityStatus(entityIdx) == 1` directly as a number or inspect `Testing.GetEntityInfo(entityIdx)` / `game_*.log` combat damage records. |

## Invocation Parameters & Raw Logs
```text
local stBefore = Testing.GetEntityStatus(engTarget)
local hpBefore = stBefore and stBefore.health or -1
-> [ERROR] attempt to index local 'stBefore' (a number value)
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Treat `Testing.GetEntityStatus(entityIdx)` as an integer status return (`1` = Active, `2` = Corpse, `0` = Invalid) and inspect `Testing.GetEntityInfo(entityIdx)` or `game_*.log` combat records rather than indexing `Testing.GetEntityStatus` as a Lua table.
