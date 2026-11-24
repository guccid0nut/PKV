---
id: FN-019
kind: engine-law
title: "CGame 5-Slot UI Trigger Ceiling, CPlayer+0x404 Ally Gold, and uint32(-1)/1000 Instant-Respawn Sentinel"
binary_or_subsystem: "cgame_x64.dll | native_guard | override/ui"
rvas: ["0x1a0cf0", "0x195bd0", "0x195f25", "0x197070", "0x19bd30"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-06
tags: [k2, cgame, scoreboard, lobby, 10v10, native_guard, respawn]
---

# FN-019: CGame 5-Slot UI Trigger Ceiling, CPlayer+0x404 Ally Gold, and uint32(-1)/1000 Instant-Respawn Sentinel

## Summary
While `cgame_x64.dll` allocates full dynamic `std::vector<int>` seat arrays on `CTeamInfo` (`+0xB0`..`+0xB8`, up to 10 seats per team on `midwars_10v10`, `devowars_10v10`, and `truedevo_10v10`), its `CUIArrays` trigger pool (`cgame_x64.dll+0x1a0cf0`) and `CGameInterfaceManager` dispatch loops (`UpdateScoreboard` `+0x195bd0`, `UpdateLobbyTeam` `+0x197070`, `UpdateHeroSelect` `+0x19bd30`) hardcode `5` per-team UI trigger slots (`0..9` total), silently dropping seats `5..9` (`players 11..20`). Additionally, `CPlayer+0x404` holds total/ally gold (`cgame_x64.dll+0x195f25`) whereas `+0x420` holds local-player reliable gold (`0` for non-local bots/allies), enemy inventory visibility must be gated through `CPlayer::CanSee` (`vtable+0x1E0`), and instant-respawn maps emit `uint32(-1) / 1000 = 4294967` on `ScoreboardPlayerRespawn{index}`, rendering clipped `"42949"` labels unless clamped.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) Joining lobbies, hero pickers, and in-game scoreboards on `10v10` (`20-player`) maps only display 10 seats (`0..9`); seats `5..9` per team cannot be clicked or viewed. (2) Reading `CPlayer+0x420` shows `0` gold for allies/bots. (3) Instant-respawn maps (`midwars_10v10`) render `"42949"` over dead hero portraits on the scoreboard and HUD portrait strip. |
| **Root Cause** | (1) `cgame_x64.dll+0x1a0cf0` registers `LobbyPlayerInfo0..9`, `HeroSelectPlayerInfo0..9`, and `ScoreboardPlayer0..9` (`uVar4 < 5` per team). (2) `cgame_x64.dll+0x195f25` reads ally gold from `*(uint16*)(pPlayer + 0x404)` when `localTeam == team \|\| isSpectator`, and gates enemy inventory slots (`0..5`, `7..8`) via `CPlayer::CanSee(pLocalPlayer, pUnit)` (`vtable + 0x1E0`). (3) Instant-respawn maps pass `0xFFFFFFFF` (`-1` ms) unsigned-divided by `1000` (`4294967`) as `ScoreboardPlayerRespawn{index}` `param0`. |
| **Fix / Workaround** | (1) Expose SEH-guarded read-only Lua C closures (`KongorSharedMem.GetTeamSlotCount`, `GetLobbyTeamSlot`, `GetScoreboardTeamSlot`) in `native/proxies/native_guard/kongor_lua_shared_mem.cpp` reading `CTeamInfo` (`+0xB0`) and `CPlayer*` by `(team, seat)` (`0..9`), reading `*(uint16*)(pPlayer + 0x404)` for ally/spectator gold and gating enemy items via `CPlayer::CanSee` (`vtable+0x1E0`). (2) Isolate sibling `20p` UI containers strictly to `playerMaxNum > 10` / `teamSlotCount > 5` with `0` `<modelpanel>` widgets in the 20p hero picker. (3) Clamp respawn countdowns to `(rsp > 0) and (600 > rsp)` in XML (`ui/game_score.package`, `ui/fe2/game_hud_common.package`) and `(rsp > 0) and (rsp < 600)` in `ui/scripts/game_new.lua`. |

## Verification Oracle
Exact commands that prove the obligations, native unit tests, and Lua contracts hold:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_evidence_obligations.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never instantiate `<modelpanel>` per-player rows in `>5v5` UI containers, never expose enemy inventory/gold in `KongorSharedMem` without `localTeam == team || isSpectator` and `CPlayer::CanSee` fog-of-war gating, and always bound K2 respawn timers to `(rsp > 0) and (600 > rsp)` (using `600 > rsp` in XML attributes to avoid raw `<` parser errors) so `uint32(-1)/1000` (`4294967`) sentinels never render.
