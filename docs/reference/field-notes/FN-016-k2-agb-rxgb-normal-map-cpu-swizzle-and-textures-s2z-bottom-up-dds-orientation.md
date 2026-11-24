---
id: FN-016
kind: rendering
title: "K2 .agb RxGB Normal-Map CPU Swizzle and textures.s2z Bottom-Up DDS Orientation"
binary_or_subsystem: "vid_d3d9_x64.dll | k2_x64.dll | textures.s2z | override"
rvas: ["0x09DA30", "0x0982D0", "0x096040", "0x0956C0", "0x04BB80", "0x685FD0"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-02
provenance: "Session"
evidence_source: "games/client/game/core/shaders/ps_3_0/mesh_color_unit_team_lightmap_spec.psh"
evidence_quote: "float3 cNormalmapColor = tex2D(normalmap_0, In.Texcoord0).agb;"
tags: [rxgb, normal_map, textures_s2z, ctexturearchive, ctexturecache, vflip, dds, tga, shadowing]
---

# FN-016: K2 .agb RxGB Normal-Map CPU Swizzle and textures.s2z Bottom-Up DDS Orientation

## Summary
In `vid_d3d9_x64.dll`, `D3D_Register2DTexture` (`+0x09DA30`) resolves textures via `CTextureArchive` (`+0x096040` / `+0x04BB80`) and `CTextureCache` (`+0x0956C0`) before falling back to loose `CBitmap::Load` (`k2_x64.dll + 0x685FD0`). Two distinct findings govern K2 normal maps and `textures.s2z` overrides:
1. **Ghidra + HLSL + Live-Verified Normal-Map Swizzle (`0x09DA30`, `0x0982D0`):** K2 HLSL normal shaders (`mesh_color_unit_team_lightmap_spec.psh:170`) sample `tex2D(normalmap_0, In.Texcoord0).agb` (`Red = 255`, `Green = +Ny`, `Blue = Nz`, `Alpha = Nx`). On an archive/cache miss for a loose `.tga`/`.png` normal map, `FUN_1800982d0` (`vid_d3d9_x64.dll + 0x0982D0`, `case 0xf`) performs the `RGB -> RxGB` swizzle on the CPU (`case 0x10` extracts 32-bit alpha into `DXT1` `*_s.dds`) and writes the compressed output into `CTextureCache` (`userdata/.../texturecache/`). Supplying an already-swizzled loose `.tga` causes runtime **double-swizzling** (`R = 255 -> Alpha = 255`, pinning $N_x = +1.0$ and biasing surface lighting sideways).
2. **Offline Empirical Asset-Diff Finding (Bottom-Up `.dds` vs Top-Down `.tga`):** Offline pixel comparison across 5 vanilla loose `.tga` hero icons (`bubbles/trophy_skin/icon.tga`, `empath/hd_empath/icon.tga`, `fade/alt7/icon.tga`, `hydromancer/alt6/icon.tga`, `pollywogpriest/alt/hero.tga`) and `heroes/rhapsody/althd/icon.tga` shows high unflipped MAE (`43.8–81.0`) against `textures.s2z` `00000000/*.dds`, whereas vertically flipping the `.dds` array (`s2z_arr[::-1]`) matches within DXT5 quantization (`max_channel_MAE <= 5.35`). *(Note: the native `CTextureArchive` / `CBitmap::LoadTGA` row-flip call site has not yet been decompiled in Ghidra; the RVAs in frontmatter apply strictly to Claim 1 and `CTextureArchive` stem lookup.)*

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) `heroes/bubbles/alt8/normal.tga` rendered with harsh lateral lighting bias (`Alpha = Nx` collapsed to `+1.0`) on the Learnatorium turntable; (2) 5 vanilla loose `.tga` hero icons appeared divergent (`MAE = 43.82–81.00`) from `textures.s2z` `00000000/*.dds` in offline audits despite representing the same artwork. |
| **Root Cause** | (1) `D3D_Register2DTexture` (`vid_d3d9_x64.dll + 0x09DA30`) invokes `FUN_1800982d0(case 0xf)` (`+0x0982D0`) on loose `.tga`/`.png` normal maps to swizzle `R -> A` and set `R = 0xFF`, so a pre-swizzled loose `.tga` overwrites `A` with `255` and writes DXT5 output to `CTextureCache` (`+0x0956C0`). (2) Offline decoded image comparison shows `textures.s2z` `.dds` entries are vertically inverted (`s2z_arr[::-1]`) relative to loose `.tga` files. |
| **Fix / Workaround** | Keep all loose normal maps in `games/client/override/` as standard tangent-space RGB `.tga` (`R = Nx ~128, G = +Ny ~128, B = Nz ~245, A = specular`), reserve `1gbr` `DXT5` pre-swizzling strictly for archived `*_rxgb.dds` builds (`TextureModernizationService.cs` / `dds_bake_rxgb_normal_map`), clear matching `<stem>_rxgb.dds` entries in `userdata/.../texturecache/` as a precautionary reset when re-testing normal maps (`CTextureCache` automatic mtime/CRC invalidation policy unverified), and allow `s2z_arr[::-1]` V-flip equivalence (`DXT5_VFLIP_MAE_TOLERANCE = 5.5`) for cross-extension `.tga`/`.png` checks in `tools/audit_k2_textures.py`. |

## Verification Oracle
Exact commands proving zero UI shadowing, zero pre-swizzled loose normal maps, V-flip equivalence, and KB/gate parity:
```powershell
python -m unittest tests/unit/tools/test_audit_k2_textures.py -v
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit_k2_textures.ps1 -Strict -Detailed
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never pre-swizzle loose `.tga`/`.png` normal maps in `games/client/override/` (`scripts/inject_override_to_s2z.ps1` and `scripts/git-pre-push.ps1` enforce `scripts/audit_k2_textures.ps1 -Strict` fail-closed), purge matching `userdata/.../texturecache/` entries as a safe precaution when visually validating normal-map edits, and test `s2z_arr[::-1]` vertical orientation before classifying cross-extension `.tga`/`.png` vs `textures.s2z` `.dds` files as divergent artwork.
