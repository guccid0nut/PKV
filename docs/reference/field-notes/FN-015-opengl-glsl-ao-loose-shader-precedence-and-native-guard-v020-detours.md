---
id: FN-015
kind: rendering
title: "OpenGL GLSL Loose-Shader Manifest Quarantine, AO Fallback Guards, and NativeGuard v0.2.0 Detours"
binary_or_subsystem: "libvid_gl2-x86_64.so | k2_x64.dll | vid_d3d9_x64.dll | version.dll"
rvas: ["0x9E7260", "0x8CD7C0", "0x029100"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-01
tags: [opengl, glsl, ambient_occlusion, manifest, native_guard, loadsample, draw2d, netcode]
---

# FN-015: OpenGL GLSL Loose-Shader Manifest Quarantine, AO Fallback Guards, and NativeGuard v0.2.0 Detours

## Summary
On Linux (`libvid_gl2-x86_64.so`) and macOS OpenGL, two compounding traps broke 3D avatars and shaders: (1) shipping `4.10.1.0.manifest.xml.zip` activated K2's retail manifest CRC whitelist, silently rejecting custom `.s2z` overrides (`resources00.s2z`) and loose `core/shaders/{vs_glsl,ps_glsl}` files, and (2) `mesh_color_*.psh` GLSL fragment shaders declared `uniform sampler2D ambient_occlusion;` inside `#if (LIGHTING_QUALITY == 0)` without `#ifdef AMBIENT_OCCLUSION` guards and lacked missing-shader counterparts (`mesh_color_unit_team_spec_Rimlight.psh`, `mesh_color_orb2.psh`, `mesh_color_tally_light.psh`, `prop_sway_grass.vsh`), causing failed GLSL links or unbound sampler fallback to pure white silhouettes on Store turntables and in-world meshes. On Windows (`version.dll` `NativeGuard`), `CSoundManager::LoadSample` (`k2_x64.dll + 0x9E7260`), `D3D_Draw2D` (`vid_d3d9_x64.dll + 0x029100`), and `0x6f` `NETCMD_RECONNECT_STATUS_RESPONSE` (`k2_x64.dll + 0x8CD7C0`) required inline detours for negative-caching missing `.wav`/`.ogg` VFS probes, guarding `0xFFFFFFFF` diffuse sampler slots (`R15D`), and draining trailing `remainingReconnectMs` bytes.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Pure white 3D avatar silhouettes on Linux/macOS Store turntables; `ReloadShaders` with `ShaderUndefine AMBIENT_OCCLUSION` broke world lighting; Windows missing-audio frame hitches (`CSoundManager::LoadSample`), `vid_d3d9_x64.dll + 0x29258` `0xC0000005` OOB read when `diffuseIdx == -1`, and `0x6f` packet unread trailing bytes. |
| **Root Cause** | `4.10.1.0.manifest.xml.zip` blocked loose/packed overrides on POSIX; OpenGL `CShaderPreprocessor` reads loose `core/shaders/{vs_glsl,ps_glsl}` directly via OS paths rather than `.s2z` archives, and unguarded `ambient_occlusion` samplers failed link/validation on Mesa/Apple GL. In `k2_x64.dll`, `LoadSample` (`+0x9E7260`) re-probed missing audio paths every frame without negative caching; `D3D_Draw2D` (`vid_d3d9_x64.dll + 0x29258`) indexed `g_Textures[0xFFFFFFFF]`; and `0x6f` (`+0x8CD7C0`) read 5 bytes of a 9-byte server packet. |
| **Fix / Workaround** | Quarantined `*.manifest.xml.zip` -> `.disabled` across repo, `hon.sh`, `WILLOWMAKER`, and `export_playtest_bundle.ps1`; shipped all 31 GLSL shaders loose on disk under `ClientPackageFiles/core/shaders/` + `override/core/shaders/` with `#ifdef AMBIENT_OCCLUSION` guards; converted 4 OpenGL-incompatible normalmap `.tga` textures to `.dds`; and installed Evidence-Dossier-verified `NativeGuard` detours at `k2_x64.dll + 0x9E7260`, `k2_x64.dll + 0x8CD7C0`, and `vid_d3d9_x64.dll + 0x029100`. |

## Verification Oracle
Exact commands proving all GLSL shaders, Evidence Dossier obligations, and 6-tier gates hold:
```powershell
python -m pytest tests/unit/test_opengl_glsl_shaders.py -q
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_evidence_obligations.ps1 -Dossier artifacts/evidence/native_guard -Strict
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never ship `*.manifest.xml.zip` in standalone client directories, always keep `core/shaders/vs_glsl/*.vsh` and `core/shaders/ps_glsl/*.psh` loose on disk in exported bundles (never exclusively packed inside `.s2z`), and guard optional material samplers (`AMBIENT_OCCLUSION`, `FALLOFF_MAP`, `OCCLUSION_MAP`) with `#ifdef` blocks in GLSL 1.20 shaders.
