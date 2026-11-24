---
id: FN-023
kind: engine-law
title: "Pre-Phase-5 AddBot Double SpawnHero and Mode-1 CBotBrain Park-Restore Lifecycle"
binary_or_subsystem: "game_x64.dll | game_shared_x64.dll | native_guard"
rvas: ["0x00069c10", "0x0007e530", "0x004f1b50", "0x001b9280"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-10
tags: [k2, cplayer, spawnhero, addbot, cbotbrain, queue-arcade]
---

# FN-023: Pre-Phase-5 AddBot Double SpawnHero and Mode-1 CBotBrain Park-Restore Lifecycle

## Summary
When an arranged server match boots with `0` human lobby groups (`GroupCount == 0`), K2 skips `GAME_PHASE_HERO_SELECT` (`1`) and enters `GAME_PHASE_HERO_LOADING` (`3`/`4`). Calling `CGameServer::AddBot` (`game_x64.dll + 0x00069c10` / server `+0x0006e2e0`) during `Phase 3/4` triggers an immediate `CPlayer::SpawnHero` (`game_shared_x64.dll + 0x004f1b50`) because `AddBot` checks `m_pGameInfo->m_eGamePhase (+0xB4) != 1` instead of `>= 5`; seconds later, `CGameServer::EndHeroLoading` (`game_x64.dll + 0x0007e530`) iterates `m_mapClients (+0x1E8)` before calling `SetGamePhase(5)` and invokes `CPlayer::SpawnHero` a second time, allocating an `items=0` twin hero while orphaning the `items=1` fountain hero on the same `m_iOwnerClientNumber (+0x6D4)`. Separately, `CBotBrain::~CBotBrain` (`game_shared_x64.dll + 0x001b9280`) invokes `CLuaScriptManager::DestroyUserDataTable` and `ReleaseUserData`, requiring any `CBotBrain*` parked aside during human slot takeover to be re-docked and freed via `CPlayer::RemoveBot` (`+0x004f62e0`) inside `CPlayer::Terminate` / pre-`UnloadWorld` while `CLuaScriptManager` is still live.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Every player and bot in a 0-human-boot Queue Arcade match spawns 2 identical heroes (`cluster=10/10` on `5v5`), leaving a Level 1 `items=1` clone frozen in fountain while the active `items=0` hero lanes without starting items; additionally, vacated human slots remain motionless statues after `ArcadeReleaseSlot`. |
| **Root Cause** | `CGameServer::AddBot` (`0x00069c10`) calls `SpawnHero` whenever `m_eGamePhase != 1`, and `EndHeroLoading` (`0x0007e530`) calls `SpawnHero` again while `m_eGamePhase < 5`; `CPlayer::SpawnHero` (`0x004f1b50`) never checks `m_uiHeroIndex (+0xB0 != 0xFFFFFFFF)`. On human takeover, calling `CPlayer::RemoveBot` destroyed `CBotBrain` permanently instead of parking it, while freeing a parked `CBotBrain` after `UnloadWorld` would touch a torn-down `CLuaScriptManager` inside `~CBotBrain` (`0x001b9280`). |
| **Fix / Workaround** | Suppress `CPlayer::SpawnHero` in `Hooked_CPlayer_SpawnHero` (`native/proxies/native_guard/dllmain.cpp`) via `Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn` when `m_eGamePhase < 5` and `+0xB0` resolves via `IGame+0x28` (`vtable+0x10` -> `vtable+0x178`) to a live unit (keeping Mode 2 `ArcadeParkUnreservedBots` on the exit path). In Mode 1 (`ActiveBots`), park `+0x730` in `ArcadeReservationEntry::parkedBotBrain`, restore `+0x730`, `+0x10`, and `+0x3E0 |= 0x0008` on `ArcadeReleaseSlot`, and flush parked brains via `CPlayer::RemoveBot` in `Hooked_CPlayer_Terminate` / pre-`CreateMatch` `ClearAllArcadeReservations`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_evidence_obligations.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never allow duplicate `CPlayer::SpawnHero` execution while `m_eGamePhase < 5` and `CPlayer + 0xB0` already resolves to a live `IUnitEntity`, and never free a detached `CBotBrain*` after `UnloadWorld` — always re-dock onto `CPlayer + 0x730` and invoke `CPlayer::RemoveBot` while `IGame::s_pGame` and `CLuaScriptManager` are live.
