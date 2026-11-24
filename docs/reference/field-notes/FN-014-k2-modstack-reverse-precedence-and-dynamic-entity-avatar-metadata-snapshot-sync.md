---
id: FN-014
kind: netcode
title: "K2 ModStack Reverse Precedence and Dynamic Entity Avatar Metadata Snapshot Sync"
binary_or_subsystem: "k2_x64.dll | libk2-x86_64-server.so | game_shared_x64.dll | libgame_shared-x86_64-server.so | cgame_x64.dll"
rvas: ["0x56C620", "0x56DE90", "0x56BD00", "0x56BDE0", "0xFDF6D0", "0xFDF7C0", "0x0F4FBA"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-27
tags: [k2, vfs, modstack, snapshot, unread_bits, desync, avatar, compel]
---

# FN-014: K2 ModStack Reverse Precedence and Dynamic Entity Avatar Metadata Snapshot Sync

## Summary
`CFileManager::SetModStack` (`libk2-x86_64-server.so + 0x56C620`, inspected via `ListMods` at `0x56DE90`) pushes `-mod` semicolon tokens left-to-right onto `m_vModStack` and evaluates VFS lookups in **reverse order** (`back()` = highest priority, `front()` = lowest priority). Placing `override` before `game` on dedicated servers (`-mod override;base;game;server`) caused `game/resources*.s2z` to shadow `games/server/override/`. When a dynamic entity (`unType >= 0x101`) resolved to different `.entity` files on client vs. server with different `IEntityDefinition::vecAvatars` counts (`[pDef+0xB8..+0xC0]`), `IGame::WriteSnapshotMetadata` (`libgame_shared-x86_64-server.so + 0xFDF6D0` / `game_shared_x64.dll + 0x56BDE0`) and `IGame::ReadSnapshotMetadata` (`0xFDF7C0` / `0x56BD00`) wrote vs. read different `floor(log2(vecAvatars.size())) + 1` bit widths inside `CEntitySnapshot`, shifting the snapshot bitstream and throwing `unread_bits` in `CGameClient::ProcessSnapshot` (`cgame_x64.dll + 0x0F4FBA`: `"You have been disconnected because your client's files do not match those on the server."`).

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Online matches on dedicated servers (`#K1`, `#D1`, `#D2`) disconnected with `CGameClient::ProcessSnapshot() - unread_bits` (`cgame_x64.dll + 0x0F4FBA`) as soon as an entity with shadowed `<altavatar>` definitions spawned (e.g., `AlunaBot` firing `Projectile_AlunaAttack` vs `Projectile_AlunaAttack_Alt10_Shadowed`). |
| **Root Cause** | `ClientModMountSpec` mounted `base;game;ClientPackageFiles;../shared;KONGOR/*;override` (`override` at `back()`, above `game`), whereas `DedicatedWorkerArguments.cs`, `GameServerManageHandler.cs`, and `P2pHostServerService.cs` mounted `override;base;game;server` (`override` below `game`). Server loaded stale `resources94.s2z` (`vecAvatars.size() == 3` -> `2` metadata bits) while client loaded `override/` (`vecAvatars.size() == 0` -> `0` metadata bits). |
| **Fix / Workaround** | Reordered `-mod` across `DedicatedWorkerArguments.cs`, `GameServerManageHandler.cs`, and `P2pHostServerService.cs` to `base;game;[KONGOR/*;]override;server` so `override` sits above `game` and `KONGOR/*` while preserving `server` at `back()` for `~:` userdata log/replay isolation, and synchronized missing `.entity` mirrors across `games/server/override/` and `games/client/override/`. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
dotnet test tests/unit/apps/COMPEL.Tests/COMPEL.Tests.csproj --filter DedicatedWorkerArguments_Build_MountsOverrideAboveGameAndKongorTiersWithServerAtTop
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never place `override` before `game` or `KONGOR/*` in any K2 `-mod` argument string (`back()` of `m_vModStack` always wins VFS lookup). Keep `<altavatar>` vectors on dynamic entities (`unType >= 0x101`) 100% identical between client and server VFS resolutions.
