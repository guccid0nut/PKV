---
id: FN-025
kind: rendering
title: "K2 Terrain texeldensity=0 White Alphamap Fallback and TERRAIN_DEREPEAT Chunk-Local UV Seams"
binary_or_subsystem: "vid_d3d9_x64.dll | vid_gl2_x64.dll | k2_x64.dll | games/client/override/ui/scripts/enfos_hud.lua"
rvas: ["0x89C90", "0x73590", "0xA1E950"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-15
tags: [k2, terrain, texeldensity, terrain-derepeat, enfos, custom-maps, shader, uv-seam]
---

# FN-025: K2 Terrain `texeldensity=0` `$white` Alphamap Fallback & `TERRAIN_DEREPEAT` Chunk-Local UV Seams

## Summary
Legacy HoN 2.x custom maps (such as `enfos.s2z`) face two compounding K2 4.x terrain rendering quirks without global shader defects:
1. When `<world texeldensity="0" ...>` is present in `worldconfig`, `D3D_RebuildTerrainChunk` (`vid_d3d9_x64.dll + 0x89C90`) skips per-chunk alphamap generation and binds `$white` (`1.0` alpha) to the terrain blend sampler while `terrain_color.psh` treats `tilealphanull` chunks as solid white, washing the entire ground out to `#FFFFFF` under bloom.
2. In `CTileMaterialMap::Load` (`k2_x64.dll + 0xA1E950`), legacy v1/v2 per-tile rotation/offset floats on disk are discarded; terrain UVs in `terrain_color.vsh` (fed by `vWorldSizes` at `vid_gl2_x64.dll + 0x73590`) are computed strictly from chunk-local tile coordinates `vTexcoord0 = vTile * (1.0 / texturescale)`. When `vid_terrainDerepeat` (`TERRAIN_DEREPEAT`) is enabled, `terrain_color.vsh` multiplies chunk-local `vTexcoord0` by `fLayer0Scale = 0.5` and `fLayer1Scale = 0.25` without world-space chunk origin offsets and cross-fades both scales (`lerp`), slicing `texturescale="16.0"` geometric floor tiles (`lair_d.tga` medallions) with `0.5` UV discontinuities along chunk boundaries and double-exposing mismatched scales.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) Blinding `#FFFFFF` white ground across `enfos` lanes and Pick Chamber; (2) After restoring terrain visibility, circular bronze temple floor medallions (`lair_d.tga` at `texturescale="16.0"`) appear chopped by hard vertical/horizontal chunk seams and ghosted at two zoom scales. |
| **Root Cause** | (1) `worldconfig` specified `texeldensity="0"`, hitting the `iTexelDensity <= 0` `$white` alphamap branch in `D3D_RebuildTerrainChunk` (`vid_d3d9_x64.dll + 0x89C90`). (2) `D3D_RebuildTerrainChunk` packs chunk-local `(local_tx, local_ty)` into vertex `Data0.w` / `Data1.w`; without `TERRAIN_DEREPEAT`, `chunkSize / 16.0` is an integer (`mod 1.0 == 0`), so UVs wrap seamlessly across chunks, whereas `TERRAIN_DEREPEAT` scales chunk-local UVs by `0.5` and `0.25`, breaking `mod 1.0` continuity at odd chunk multiples. |
| **Fix / Workaround** | Never modify global `terrain_color.vsh` / `terrain_color.psh` shaders. Instead: (1) Set `texeldensity="4"` in the map's `worldconfig` inside `enfos.s2z` so K2 allocates valid `CTerrainChunk` alphamap textures; (2) Temporarily set `vid_terrainDerepeat = false` when entering `enfos` (`EnfosHUD.ApplyTerrainAlignment` in `games/client/override/ui/scripts/enfos_hud.lua`), saving the user's preference in `cg_enfosSavedDerepeat` / `cg_enfosDerepeatOverride` and restoring it automatically on match exit, interface unload, and Main Menu entry (`main_v2.lua`). |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 -Owner fn025 -- .\src\tools\lua-verifier\bin\Release\net10.0\LuaVerifier.exe --script tests/client_lua/test_custom_maps_ui_and_lobby_guards.lua
```

## Prevention Invariant
When repairing legacy HoN 2.x custom maps in K2 4.x: (1) always audit `<world texeldensity="...">` in `worldconfig` and patch `texeldensity="0"` to `"4"` inside the map `.s2z` rather than altering global terrain shaders; and (2) when a map uses large geometric architectural tiles (`texturescale="16.0"` medallions/plazas) that tear along chunk boundaries under `vid_terrainDerepeat`, scope `vid_terrainDerepeat = false` to that map's lifecycle with persistent CVar save-and-restore on both `GamePhase <= 3` and Main Menu entry.
