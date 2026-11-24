---
id: FN-009
kind: rendering
title: "Post-World / Pre-UI Render Pass Hooking, Offscreen RT Filtering, and Live CCamera Frustum Parity for Depth-Occluded 3D Overlays"
binary_or_subsystem: "k2_x64.dll | vid_d9_x64.dll | version.dll (NativeGuard)"
rvas: ["0x6F7E20", "0x91FA80", "0x951020"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [k2, rendering, depth-buffer, occlusion, camera-frustum, native-guard, d3d9, vulkan]
---

# FN-009: Post-World / Pre-UI Render Pass Hooking, Offscreen RT Filtering, and Live CCamera Frustum Parity for Depth-Occluded 3D Overlays

## Summary
Drawing custom 3D overlays (range indicators, pathing gizmos, world-space markers) inside `IDirect3DDevice9::Present` (`EndScene`) causes overlays to float on top of terrain cliffs, fog-of-war geometry, and 2D pause/shop menus (`CInterfaceManager::Draw` at `k2_x64.dll + 0x91FA80`). Additionally, computing projection matrices from static config constants causes world-space overlays to skate across terrain when the camera zooms or pitches. To achieve natural world depth occlusion and menu layering, 3D overlays must execute at the **return** of the primary backbuffer world scene pass (`CSceneManager::Render` at `k2_x64.dll + 0x6F7E20`), filter out offscreen render targets, preserve the scene Z-buffer, and read live `CCamera` frustum bounds every frame.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Custom 3D world-space gizmos draw over trees/cliffs without depth occlusion, remain visible on top of fullscreen UI menus (`main_v2.interface`, Escape menu, shop), or drift/skate relative to ground coordinates during camera zoom or `<modelpanel>` rendering. |
| **Root Cause** | (1) At `Present` / `EndScene`, 2D UI has already been composited and the scene depth buffer (`Z-buffer`) has either been cleared or overwritten by post-processing / UI passes. (2) `CSceneManager::Render` is invoked multiple times per frame — once for offscreen water reflection/refraction, shadow maps, or `<modelpanel>` scene buffers (`k2_x64.dll + 0x951020`), and once for the primary backbuffer viewport. (3) Static `near`/`far`/FOV constants diverge from the active `CCamera` frustum during zoom transitions. |
| **Fix / Workaround** | Enforce the 3-part Post-World Overlay Contract in `NativeGuard` / Vulkan layer hooks: (1) **Hook Post-World Return & Filter Offscreen RTs:** Hook the return of the main world scene render pass and verify `GetRenderTarget(0)` matches the primary backbuffer surface before drawing (ignoring offscreen water/shadow/`<modelpanel>` passes) so 2D UI (`CInterfaceManager::Draw`) naturally draws on top afterward. (2) **Preserve Scene Z-Buffer:** Never call `Clear(D3DCLEAR_ZBUFFER)` before the overlay pass; draw with `D3DRS_ZENABLE = TRUE` and `D3DRS_ZFUNC = D3DCMP_LESSEQUAL` so terrain and cliff geometry naturally occlude the overlay. (3) **Live `CCamera` Frustum Parity:** Read `zNear`, `zFar`, `fov`, aspect ratio, and view/origin vectors directly from the live `CCamera` struct each frame rather than hardcoded constants. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
```

## Prevention Invariant
Never render 3D world-space overlays inside `Present` / `EndScene` or with hardcoded `zNear`/`zFar` projection constants; always hook the post-world / pre-UI main backbuffer pass with intact scene depth and live `CCamera` frustum values.
