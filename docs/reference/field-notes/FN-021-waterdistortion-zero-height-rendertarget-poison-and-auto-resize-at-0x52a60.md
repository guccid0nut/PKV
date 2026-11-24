---
id: FN-021
kind: rendering
title: "WaterDistortion Zero-Height RenderTarget Poison and Auto-Resize at 0x52a60"
binary_or_subsystem: "vid_d3d9_x64.dll | vid_vulkan_x64.dll | DXVK"
rvas: ["0x00052320", "0x00052880", "0x00052a60"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-09
tags: [k2, vulkan, dxvk, waterdistortion, rendertarget, midwars]
---

# FN-021: WaterDistortion Zero-Height RenderTarget Poison and Auto-Resize at 0x52a60

## Summary
In `vid_d3d9_x64.dll` (`CWaterDistortionMap::Init`, RVA `0x00052320`, and `CWaterDistortionMap::Render`, RVA `0x00052a60`), K2 computes `$waterdistortion` dimensions via `viewportW >> 1` and `viewportH >> 1`. When a `1`-pixel-tall UI widget scene pass triggers initialization (`1 >> 1 == 0`), DXVK rejects `CreateTexture(..., 317, 0, ..., D3DUSAGE_RENDERTARGET)` with `D3DERR_INVALIDCALL` (`0x8876086c`), leaving `$waterdistortion` bound to K2's neon-magenta `$checker` fallback texture. Clamping `0`-width/`0`-height render targets and depth-stencil surfaces to `>= 1` across `IDirect3DDevice9`/`9Ex` vtable slots `23`, `28`, `29`, `129`, and `131` in `vid_vulkan_spy.cpp` prevents `$checker` poisoning while allowing `0x00052a60` to auto-resize `$waterdistortion` to half-viewport (`960x540` at `1080p`) on the very next world scene pass.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Mid Wars / Caldavar fountain pools and rivers render solid neon pink / magenta (`#FF40FF`), accompanied by `CD3D9Texture::Register2DTexture($waterdistortion, 317, 0) - CreateTexture failed (D3DERR_INVALIDCALL)` in `console.log`. |
| **Root Cause** | `CWaterDistortionMap::Init` (`vid_d3d9_x64.dll + 0x00052320`) divides the active scene viewport by 2 (`height >> 1`). A `1px`-tall UI pass produces `height == 0`, causing DXVK `CreateTexture` to fail and K2's texture manager to bind the magenta `$checker` fallback texture permanently for that material registration. |
| **Fix / Workaround** | Intercept `IDirect3DDevice9` + `IDirect3DDevice9Ex` vtable slots `23` (`CreateTexture` when `Usage & (D3DUSAGE_RENDERTARGET \| D3DUSAGE_DEPTHSTENCIL)`), `28` (`CreateRenderTarget`), `29` (`CreateDepthStencilSurface`), `129` (`CreateRenderTargetEx`), and `131` (`CreateDepthStencilSurfaceEx`) in `native/drivers/vid_vulkan/src/vid_vulkan_spy.cpp`, clamping `0` width/height to `1` and logging caller module + RVA telemetry (`[VULKAN_3D]`). On the next world pass, `CWaterDistortionMap::Render` (`0x00052a60`) detects `DAT_180431d7c != viewportW >> 1 || DAT_180431d80 != viewportH >> 1` and automatically recreates `$waterdistortion` at full half-viewport resolution (`960x540`). |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Always guard dynamic `D3DUSAGE_RENDERTARGET` and `D3DUSAGE_DEPTHSTENCIL` creation against `0`-dimension sub-viewport bit-shifts (`dim >> 1 == 0`) in `vid_vulkan_spy.cpp` across both base `IDirect3DDevice9` and extended `IDirect3DDevice9Ex` vtable entries.
