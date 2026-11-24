---
id: FN-004
kind: rendering
title: "DXVK 3.0.2 Binary Pipeline Cache (dxvk.bin + dxvk.lut) and Spatial FSR 1.0 EASU+RCAS"
binary_or_subsystem: "d3d9.dll (DXVK 3.0.2) | dxvk.conf | vid_d9_x64.dll"
rvas: ["0x000000"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [dxvk, vulkan, shader-cache, fsr, d3d9, performance]
---

# FN-004: DXVK 3.0.2 Binary Pipeline Cache (`dxvk.bin` + `dxvk.lut`) and Spatial FSR 1.0 EASU+RCAS

## Summary
Project Kongor's custom DXVK 3.0.2 (`d3d9.dll` translating `vid_d9_x64.dll` D3D9 calls to Vulkan) replaces legacy `.dxvk-cache` files with a two-file binary state cache (`hon_x64.dxvk.bin` + `hon_x64.dxvk.lut`), leverages `VK_EXT_graphics_pipeline_library` for asynchronous pipeline compilation, and provides a native spatial FSR 1.0 EASU+RCAS upscaling pass configured via `dxvk.conf`.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | First-match shader compilation micro-stutter when pipeline caches are missing or invalidated, or blurry post-process scaling on high-DPI displays. |
| **Root Cause** | Legacy `.dxvk-cache` paths are ignored by DXVK 3.0.2's new binary `.dxvk.bin`/`.dxvk.lut` serializer unless `dxvk.shaderCachePath` and `VK_EXT_graphics_pipeline_library` are properly configured in `games/client/dxvk.conf`. |
| **Fix / Workaround** | Preserve `hon_x64.dxvk.bin` and `hon_x64.dxvk.lut` in the configured cache directory and enable spatial FSR 1.0 (`dxvk.fsrEffect = spatial`) in `games/client/dxvk.conf`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never delete `*.dxvk.bin` or `*.dxvk.lut` state cache artifacts during routine VFS cache cleanups unless explicitly testing cold-start shader compilation.
