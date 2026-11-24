---
id: FN-020
kind: engine-law
title: "EndHeroLoading CPlayer 0x668 Disconnect Rule and Queue Arcade Drop-In State"
binary_or_subsystem: "game_shared_x64.dll | game_x64.dll | native_guard"
rvas: ["0x0007e530", "0x00592170"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-09
tags: [k2, cplayer, queue-arcade, disconnect, clan-tag, ping]
---

# FN-020: EndHeroLoading CPlayer 0x668 Disconnect Rule and Queue Arcade Drop-In State

## Summary
In `game_shared_x64.dll` (`CGameInfo::EndHeroLoading`, RVA `0x0007e530`) and `game_x64.dll` (`CServerGameLib::Frame`, RVA `0x00592170`), any `CPlayer` slot with `m_bMatchmakingPostInitialized == 0` (`+0x668`) when `sv_precreateArrangedPlayers == 0` at the Phase `4 -> 5` transition is stamped disconnected (`+0x3DE |= 0x0101`, `+0x430 = gameTime`, `+0x438 = gameTime + 300000`), triggering a `300s` disconnect countdown (`276s -> 146s`) and `Ping: 0` on late human drop-ins. Keeping `+0x668 = 1` across `ArcadeReserveSlotSeh` and `ArcadeReleaseSlotSeh`, clearing `0x0101 | 0x0040` on `+0x3DE`, resetting disconnect timers (`+0x420 = 0`, `+0x430 = 0xFFFFFFFF`, `+0x434 = 0`, `+0x438 = 0xFFFFFFFF`), and stamping `[TAG]Name` into `CNetworkResourceManager` slot `+0x2D8` (`+0x2DA = L""`, `+0x2DC = L""`, matching `CPlayer::Initialize` at `0x004f6a40`) preserves connected status, live RTT ping updates, Scoreboard `[TAG]Name` display, and chat channel roster identity without bare-name duplicates.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Human player hot-joining an active Queue Arcade bot match appears grayed-out on the Scoreboard with a `300s` (`276s -> 146s`) disconnect timer, `Ping: 0`, and bare account name (causing duplicate entries in chat channels on lobby return). |
| **Root Cause** | `ArcadeReserveSlotSeh` cleared `CPlayer + 0x668` before the client connected; when `EndHeroLoading` (`game_shared_x64.dll + 0x0007e530`) ran at `Phase 4 -> 5`, it executed `CPlayer::Disconnected` on the slot (`+0x3DE |= 0x0101`, `+0x438 = now + 300000`). Additionally, stripping `[TAG]` out of `m_unNameIndex` (`+0x2D8`) diverged from `CPlayer::Initialize` (`game_shared_x64.dll + 0x004f6a40`), which writes `CClientConnection::m_sName` (`[TAG]Name`) directly to `+0x2D8` and `TSNULL` (`L""`) to `+0x2DA` / `+0x2DC`. |
| **Fix / Workaround** | Keep `CPlayer + 0x668 = 1` in `ArcadeReserveSlotSeh` and `ArcadeReleaseSlotSeh` (`native/proxies/native_guard/queue_arcade_hooks.cpp`), clear `0x0101 | 0x0040` on `+0x3DE`, reset `+0x420 = 0`, `+0x430 = 0xFFFFFFFF`, `+0x434 = 0`, `+0x438 = 0xFFFFFFFF`, and stamp `[TAG]Name` directly into `CNetworkResourceManager` slot `+0x2D8` (`+0x2DA = L""`, `+0x2DC = L""`). |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_evidence_obligations.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never flip `CPlayer + 0x668` (`m_bMatchmakingPostInitialized`) to `0` on an unattached Queue Arcade slot prior to `EndHeroLoading`, and always stamp clan-prefixed `[TAG]Name` into `m_unNameIndex` (`+0x2D8`) matching `CPlayer::Initialize` (`0x004f6a40`) so Scoreboard and client `cgame` preserve `[TAG]Name` identity.
