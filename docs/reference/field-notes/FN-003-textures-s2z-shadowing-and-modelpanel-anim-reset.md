---
id: FN-003
kind: rendering
title: "CResourceManager textures.s2z Legacy Shadowing and CModelPanel Static anim=idle Reset Freeze"
binary_or_subsystem: "k2_x64.dll | vid_vulkan_x64.dll | games/client/game/textures.s2z | override/ui"
rvas: ["0x94EB70", "0x951020"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-02
tags: [k2, textures-s2z, modelpanel, animation, scenebuffer, vfs, ui]
---

# FN-003: `CResourceManager` `textures.s2z` Legacy Shadowing and `CModelPanel` Static `anim="idle"` Reset Freeze

## Summary
Two K2 UI/rendering behaviors routinely cause silent visual regressions if not accounted for: (1) `CResourceManager::RegisterTexture` gives legacy downscaled entries inside `games/client/game/textures.s2z` (`00000000/ui/...`) special lookup precedence that shadows identically named overrides in `games/client/override/`, and (2) setting `anim="idle"` as a static XML attribute on `<modelpanel>` causes `CModelPanel::Frame` (`k2_x64.dll + 0x951020`) to re-invoke `CModelPanel::SetAnim` (`k2_x64.dll + 0x94EB70`) -> `CSkeleton::StartAnim` every tick, resetting `StartTime` to `Host.m_uiTime` ($\Delta t \equiv 0$) and freezing the 3D preview unless deduplicated. On Project Kongor's default `vid_vulkan` driver (`vid_vulkan_x64.dll`), `Hook_CModelPanel_SetAnim` (`native/drivers/vid_vulkan/src/vid_vulkan_spy.cpp`) already intercepts `k2_x64.dll + 0x94EB70` and deduplicates redundant per-frame `SetAnim` calls natively across all existing XML `<modelpanel>` widgets without risky bulk XML edits.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) Modified UI `.dds` texture in `override/ui/` fails to appear in-game; (2) `<modelpanel>` 3D hero preview is frozen on frame 0 or stutters every frame when running without `vid_vulkan_x64.dll`. |
| **Root Cause** | (1) `textures.s2z` `00000000/` mip-prefix resolution in `CResourceManager::RegisterTexture` bypasses normal folder override order; (2) `CModelPanel::Frame` (`+0x951020`) checks the static `anim` property each tick and calls `CModelPanel::SetAnim` (`+0x94EB70`), restarting skeletal animation from `t = 0`. |
| **Fix / Workaround** | (1) Use a versioned texture basename (e.g. `tab38_*.dds`) not present in `textures.s2z` and update the `.package`/`.interface` reference; (2) Existing `<modelpanel anim="idle">` widgets are protected globally by `Hook_CModelPanel_SetAnim` (`k2_x64.dll + 0x94EB70` in `vid_vulkan_spy.cpp` — do **not** bulk-strip `anim="idle"` from existing XML, as empty anim names during `CModelPanel::Frame` can delete active skeletons). For newly authored `<modelpanel>` widgets, prefer `onloadlua="self:SetAnim('idle')"` / `onshowlua="self:SetAnim('idle')"`. Only set `scenebuffer="false"` on simple non-refractive panels—**never** disable `scenebuffer="true"` on panels rendering water refraction (`water.mdf` `$scene`) or `postoutline` / `posteffects`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit_k2_textures.ps1
```

## Prevention Invariant
Always check `textures.s2z` for colliding basenames before overriding UI textures, rely on `Hook_CModelPanel_SetAnim` (`+0x94EB70`) rather than bulk-editing existing `<modelpanel anim="...">` XML, and preserve `scenebuffer="true"` whenever `$scene` refraction or `postoutline` post-processing is used.
