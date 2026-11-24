---
id: FN-026
kind: rendering
title: "Unindexed MDF spawnevent StartEffect Orphan CEffectThread Handle Storm on ModelPanel Context Teardown"
binary_or_subsystem: "k2_x64.dll | games/client/override/ui/scripts/newui/lobby_v2.lua | games/client/override/ui/scripts/newui/hero_definitions_cache.lua"
rvas: ["0x951020", "0x94EB70"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-20
tags: [k2, modelpanel, ceffectthread, iresourcelibrary, lobby-v2, spawnevent, effect-leak]
---

# FN-026: Unindexed MDF `spawnevent` `StartEffect` Orphan `CEffectThread` Handle Storm on `ModelPanel` Context Teardown

## Summary
When a K2 `<modelpanel>` (`CModelPanel`, `k2_x64.dll + 0x951020`) loads a `.mdf` containing an unindexed `<spawnevent cmd="StartEffect ..."/>` (no explicit channel index `0..9`, e.g., `heroes/xalynx/alt_eucliwoodhellscythe/model.mdf` spawning `effects/weapon.effect` with nested `<model .../>` emitters), K2 attaches the spawned `CEffectThread` to unindexed effect slot `-1`. Subsequent Lua calls to `SetEffect('')` and `SetEffect(ch, '')` (`0..9`) cannot terminate channel `-1`, and calling `ResourceManager.DeleteContext('ui:lobby_avatars', false)` (`unregisterOrphans=false`) leaves the `.effect` handle registered only in `client:prevgame_heroes`. Once match load invokes `res_unloadHeroResources`, K2 unregisters the `.effect` (`IResourceLibrary(15)`) while the orphaned `CEffectThread` continues ticking every frame, flooding `console.log` with thousands-per-second `Error: IResourceLibrary(15)::Get(...) - Handle has been unregistered` until I/O stalls or crashes the client.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Previewing `Hero_Xalynx.alt_eucliwoodhellscythe` (`Eucliwood Hellscythe`) in Hero Pick (`lobby_v2`), switching avatars (`Alt8` -> `Alt7`), and entering a match triggers `1,900+`/sec `Error: IResourceLibrary(15)::Get(0x352) - Handle has been unregistered` spam in `console.log`, stalling the main render/UI loop (`0`-byte `hang_*.dmp`, silent exit `0x0`). |
| **Root Cause** | (1) `heroes/xalynx/alt_eucliwoodhellscythe/model.mdf` fires an unindexed `<spawnevent cmd="StartEffect effects/weapon.effect"/>` onto channel `-1`, and `weapon.effect` contains nested `<model>` emitters whose sub-handles outlive standard channel `0..9` clears. (2) `Lobby_V2:SetUIStage` / `Lobby_V2:Hide` only cleared `SetEffect('')` (channel `0`) without clearing multi-effect slots `0..9` (`SetEffect(ch, '')`) or redirecting broken unindexed `spawnevent` preview MDFs via `ResolveSafeStoreMdf`. |
| **Fix / Workaround** | (1) Provide a clean `games/client/override/heroes/xalynx/alt_eucliwoodhellscythe/preview.mdf` (indexed channel `0` `StartEffect effects/weapon.effect 0` or direct `storepassiveeffect`) and simplified `effects/weapon.effect` override, mapped in `HeroDefinitionsCache.StoreBrokenMdfFallbackMap`. (2) Route `lobby_v2.lua` avatar preview models through `HeroDefinitionsCache.ResolveSafeStoreMdf` and clear all effect channels (`SetEffect('')` plus `for ch = 0, 9 do SetEffect(ch, '') end` before `SetModel('')`) in `ClearLobbyModelPanel` on stage transitions (`SetUIStage`), `Hide()`, and avatar swaps. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_client_lua.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never load raw gameplay `model.mdf` files that contain unindexed `<spawnevent cmd="StartEffect ..."/>` (missing channel index `0..9`) directly into UI `<modelpanel>` widgets (`Store` or `Lobby_V2`). Always route preview models through `HeroDefinitionsCache.ResolveSafeStoreMdf` to an indexed `preview.mdf` override, and always clear effect channels `0..9` (`SetEffect(ch, '')`) prior to `SetModel('')` before tearing down any `ui:*` resource context.
