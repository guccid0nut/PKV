---
id: MCP-276
server: kongor
tool: kongor_client_practice
category: warm-reuse-lua-api
title: "kongor_client_practice warm reuse invalidated by GetGamePhase()==3 and SpawnHero no-op on already-spawned player"
status: resolved
verified_by: "ClientPracticeHandlerTests.EvaluateWarmProbeResponse_EnforcesMandatoryInvalidationList"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_client_practice, warm-reuse, k2-testing-lua]
prevention_rule: "Probe K2 UI Lua via UIGamePhase() (>=5), Testing.GetMountedWorld()/GetMap(), ViewingReplay()/ViewingStreaming(), and Testing.PlayerHasValidHero(); swap heroes in-place via Testing.Delete + Testing.Precache + Testing.SpawnUnit + Testing.SetPlayerHero + Testing.LevelUp."
---

# MCP-276: kongor_client_practice warm reuse invalidated by GetGamePhase()==3 and SpawnHero no-op on already-spawned player

## Summary
During live `kongor_client_practice` verification on an active Caldavar practice match, a second `kongor_client_practice` call reported `warmReused: false` with `warmReuseInvalidationReason: "not_in_active_match_phase_3"` and took `18.6s` (cold restart) instead of `< 5s`. Live `kongor_client_eval_lua` triangulation against `hon_x64.exe` proved two K2 UI Lua contract facts: (1) in UI Lua, `GetGamePhase()` returns normalized phase `3` during an active match while `UIGamePhase()` returns raw K2 phase `5`/`6`, `GetMap()` / `Testing.GetMountedWorld()` returns `'caldavar'` (`GetMapName()` is nil), and `ViewingReplay()` / `ViewingStreaming()` + `Testing.PlayerHasValidHero()` expose replay/hero state (`GetPlayerHero` is server-only); and (2) `Cmd('SpawnHero <Hero>')` is a no-op once the local player already owns a live hero entity, whereas K2's native `Testing` table (`Testing.Delete`, `Testing.Precache`, `Testing.SpawnUnit`, `Testing.SetPlayerHero`, `Testing.LevelUp`, `Testing.GiveItem`) swaps heroes, levels, and items in-place in `< 10 ms`. Fixed in `ClientPracticeHandler.cs`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_practice` |
| **Expected** | Consecutive `kongor_client_practice` calls on the same active practice map reuse the loaded world (`warmReused: true`), replace the old hero cleanly, apply `level` and `items`, and complete in `< 5.0 s`. |
| **Actual (Symptom)** | `{"warmReused":false,"warmReuseInvalidationReason":"not_in_active_match_phase_3","executionDurationMs":18597.68}` and `level: 6` stayed at level 1 when `LevelUp 5` was sent instead of `Testing.LevelUp(heroEnt)`. |
| **Root Cause** | `warmProbeLua` queried `GetGamePhase()` (`3` instead of `UIGamePhase() == 6`), `GetMapName()` (`nil` instead of `Testing.GetMountedWorld()`/`GetMap()`), and `GetPlayerHero()` (`nil` in UI Lua), and `heroSpawnLua` relied on `SpawnHero`/`Morph 0` instead of K2's native `Testing.*` practice API. |
| **Fix / Remediation** | Updated `warmProbeLua` and `heroSpawnLua` in `src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs` to query `UIGamePhase()`, `Testing.GetMountedWorld()`/`GetMap()`, `ViewingReplay()`/`ViewingStreaming()`, and `Testing.PlayerHasValidHero()`, and perform in-place hero swaps, level-ups, and item grants through `Testing.Delete` + `Testing.Precache` + `Testing.SpawnUnit` + `Testing.SetPlayerHero` + `Testing.LevelUp` + `Testing.GiveItem`. |

## Invocation Parameters & Raw Logs
```json
{
  "liveTriangulation": "UIGamePhase()=6 | GetGamePhase()=3 | Testing.GetMountedWorld()='caldavar' | Testing.IsPracticeGame()=true | Testing.SpawnUnit+SetPlayerHero=4.62ms"
}
```

## Verification / Pre-Flight Oracle
Exact command that verifies this fix:
```powershell
powershell.exe -NoProfile -File scripts/Invoke-KongorTestLock.ps1 -Owner mcp276 -- dotnet test tests/unit/tools/Kongor.Mcp.Tests/ --filter ClientPracticeHandlerTests
```

## Prevention Invariant
Probe K2 UI Lua via UIGamePhase() (>=5), Testing.GetMountedWorld()/GetMap(), ViewingReplay()/ViewingStreaming(), and Testing.PlayerHasValidHero(); swap heroes in-place via Testing.Delete + Testing.Precache + Testing.SpawnUnit + Testing.SetPlayerHero + Testing.LevelUp.
