---
id: FN-024
kind: crash-triage
title: "CVisibilityMap::FillBitmap Stale Static Half-Dimensions & 8-Bit Giant-Map Truncation"
binary_or_subsystem: "game_shared_x64.dll | version.dll (NativeGuard)"
rvas: ["0x53B1C0", "0x53BB50", "0x53BD40", "0x53C267", "0x77DEE8", "0x77DEF0", "0x77DEF4"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-13
tags: [k2, game-shared, fog-of-war, pebbles-duel, crash, native-guard, map-transition]
---

# FN-024: `CVisibilityMap::FillBitmap` Stale Static Half-Dimensions & 8-Bit Giant-Map Truncation

## Summary
In `game_shared_x64.dll`, `CVisibilityMap::FillBitmap` (`+0x53B1C0`) caches the Fog of War v2 half-grid dimensions in file-scope statics `s_uiHalfWidth` (`+0x77DEF0`) and `s_uiHalfHeight` (`+0x77DEF4`), only recomputing them when `s_iRingSize` (`+0x77DEE8`) is `0` or one of the three FoW CVar `m_bModified` bytes (`+0x77D7B4`, `+0x77D994`, `+0x77DB74`) is non-zero. Transitioning within a single client session from a larger map (`midwars` `256x256`, `halfW = 128`, `0x10000`-byte `CBitmap`) to a smaller map (`pebbles_duel` `128x128`, `halfW = 64`, `0x4000`-byte `CBitmap`) leaves `s_uiHalfWidth = 128`, causing `FUN_18053BD40` (`+0x53C267`) to stride `512` bytes per row instead of `256` and fault with `0xC0000005` (`WRITE` past the `0x4000`-byte `CBitmap` allocation). Additionally, the native CVar init block masks `(*(uint*)this >> 1) & 0xFF`, truncating `512x512` giant-map half-dimensions (`256`) to `0`.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Immediate `0xC0000005` (`EXCEPTION_ACCESS_VIOLATION` write) at `game_shared_x64.dll + 0x53C267` inside `FUN_18053BD40` (called by `CVisibilityMap::FillBitmap` `+0x53B1C0` -> `FUN_18053BB50` `+0x53BB50`) when loading a `128x128` map such as `pebbles_duel` after playing a `256x256` map, or black/unrevealed FoW on `512x512` giant maps. |
| **Root Cause** | `CVisibilityMap::FillBitmap` (`+0x53B1C0`) gates recomputation of `s_uiHalfWidth` (`+0x77DEF0`) and `s_uiHalfHeight` (`+0x77DEF4`) on `s_iRingSize == 0 \|\| m_bModified != 0` instead of comparing against `this->m_iWidth >> 1` and `this->m_iHeight >> 1`, and applies `& 0xFF` when writing `s_uiHalfWidth` / `s_uiHalfHeight`. Note: setting `s_iRingSize = 0` (`+0x77DEE8`) before calling the original function re-triggers that `& 0xFF` truncation on giant maps. |
| **Fix / Workaround** | Hook `CVisibilityMap::FillBitmap` in `native/proxies/native_guard/vfs_and_hooks.cpp` (and `_ZN14CVisibilityMap10FillBitmapER7CBitmaphh` in `native/proxies/native_guard/linux_native_guard.cpp`): verify `CBitmap` dimensions match `CVisibilityMap`, set `cg_fogofwarRingSize.m_bModified = 1` (`+0x77D7B4`) on standard map transitions (`halfW <= 255 && halfH <= 255`), and for giant maps (`halfW > 255 \|\| halfH > 255`) compute the darkness LUT / ring-size (`>= 1`) globals directly, clear all three `m_bModified` flags, and write full 32-bit `halfW` / `halfH` to `+0x77DEF0` / `+0x77DEF4`. Also perturb-and-restore `cg_fogofwarDarkness_min` in `loading_v2.lua` and `game_new.lua` as cross-platform defense-in-depth. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_evidence_obligations.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 -Owner fn024 -- powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Whenever hooking a K2 rendering or visibility routine that caches map-dependent static globals gated by CVar `m_bModified` flags, always compare live instance dimensions directly against the module's cached static fields on every call, and never trigger a native re-init block that masks 32-bit map dimensions with `& 0xFF` when `halfWidth > 255`.
