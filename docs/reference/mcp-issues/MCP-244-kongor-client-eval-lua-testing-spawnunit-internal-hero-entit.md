---
id: MCP-244
server: kongor
tool: kongor_client_eval_lua
category: lua-eval
title: "KONGOR-CLIENT-EVAL-LUA-TESTING-SPAWNUNIT-INTERNAL-HERO-ENTITY-NAME"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: []
occurrences: 27
date: 2026-10-25
last_seen: 2026-11-17
tags: [kongor, kongor_client_eval_lua, lua-eval]
prevention_rule: "Always pass canonical internal K2 hero entity names (e.g. Hero_Frosty instead of Hero_Glacius, Hero_Rocky instead of Hero_Pebbles) and call Testing.Precache(entityName) before Testing.SpawnUnit."
---

# MCP-244: KONGOR-CLIENT-EVAL-LUA-TESTING-SPAWNUNIT-INTERNAL-HERO-ENTITY-NAME

## Summary
Calling `Testing.SpawnUnit("Hero_Glacius", ...)` in `kongor_client_eval_lua` throws `Entity not found` because Glacius's internal K2 hero entity key is `Hero_Frosty` rather than `Hero_Glacius`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Spawn an enemy dummy hero via `Testing.SpawnUnit`. |
| **Actual (Symptom)** | `[ERROR] [string "..."]:3: Entity not found`. |
| **Root Cause** | Legacy S2 heroes use internal codenames (`Hero_Frosty`, `Hero_Rocky`, `Hero_Hiro`, `Hero_DiseasedRider`) rather than localized display names. |
| **Fix / Remediation** | Query `Testing.GameEntityExists(name)` or use canonical internal hero entity names (`Hero_Frosty`, `Hero_Pyromancer`, `Hero_Armadon`) and call `Testing.Precache(name)` before `Testing.SpawnUnit`. |

## Invocation Parameters & Raw Logs
```text
local dummy = Testing.SpawnUnit("Hero_Glacius", -1, 2, 3420, 3200)
-> [ERROR] Entity not found
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Always pass canonical internal K2 hero entity names (e.g. `Hero_Frosty` instead of `Hero_Glacius`, `Hero_Rocky` instead of `Hero_Pebbles`) and call `Testing.Precache(entityName)` before `Testing.SpawnUnit`.
