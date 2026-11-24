---
id: FN-017
kind: engine-law
title: "K2 Idempotent UITrigger.CreateTrigger Registry vs XML <trigger> Ownership and Procedural $ Texture Prefix Rules"
binary_or_subsystem: "k2_x64.dll | override/ui | override/**/*.material"
rvas: ["0xBD7580", "0xA55BD0", "0xA56430", "0x896190"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-05
provenance: "Session"
evidence_source: "games/client/override/ui/movf.package"
evidence_quote: "for i=1,#n do UITrigger.CreateTrigger(n[i]) end"
tags: [uitrigger, xml_trigger, cinterface, procedural_texture, getsystempath, material, movf]
---

# FN-017: K2 Idempotent UITrigger.CreateTrigger Registry vs XML `<trigger>` Ownership and Procedural `$` Texture Prefix Rules

## Summary
Static decompilation of `k2_x64.dll` in Ghidra (`ProjectKongor.rep`) paired with live dual-map (`truedevo` -> `caldavar`) verification establishes two K2 client laws governing shared UI packages and procedural material samplers:

1. **XML `<trigger>` Unconditional Allocation vs `UITrigger.CreateTrigger` Idempotency (`0xBD7580`, `0xA55BD0`, `0xA56430`):**
   - The XML `<trigger name="X"/>` processor (`FUN_180bd7580`, RVA `0xBD7580`) unconditionally allocates `new CUITrigger(name, true)` (`FUN_180a56430`, RVA `0xA56430`) and appends the pointer to the loading `CInterface`'s owned vector (`CInterface + 0x398`). When a shared `.package` (such as `ui/movf.package` or `ui/specui/specui_base_interface.package`) is included by both `game_legacy.interface` and `game_spectator.interface`, the second load collides in `g_pUITriggerRegistry` (`DAT_181261bc8`) with `Error: A UI trigger named "..." already exists.`, AND unloading either `CInterface` destroys owned triggers out from under the surviving interface.
   - `UITrigger.CreateTrigger(name)` (`FUN_180a55bd0`, RVA `0xA55BD0`) looks up `name` in `g_pUITriggerRegistry` (`DAT_181261bc8`) first: if registered, it returns the existing `CUITrigger*` with zero allocations and zero error logs; if absent, it constructs `new CUITrigger(name, true)` identically (preserving K2 UICmd variable evaluation) without binding lifetime destruction to a single `CInterface`.
2. **Procedural `$` Texture Names Reject File Extensions in `CFileManager::GetSystemPath` (`0x896190`):**
   - K2 built-in procedural textures (`$flat`, `$white`, `$black`, `$checker`, `$yellow`) are intercepted by leading `$` before VFS lookup ONLY when bare (no file extension). Appending `.tga` (e.g., `texture="$flat.tga"` or `texture="$white.tga"` in `.material` `<sampler>` nodes) routes the string into `CFileManager::GetSystemPath` (`FUN_180896190`, RVA `0x896190`), which rejects leading `$` on filesystem paths and logs `Warning: Invalid starting character for path: $flat.tga` once per mounted VFS archive.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) `19x` `Error: A UI trigger named "movf_*" already exists.` and `2x` `Error: A UI trigger named "MSPlayerInventoryTip*" already exists.` on every match load/reload; (2) `21–38x` `Warning: Invalid starting character for path: $flat.tga` across world props (`world/props/fog_grass/materialdead.material`) and 18 hero/alt `.material` files. |
| **Root Cause** | (1) `FUN_180bd7580` (`k2_x64.dll + 0xBD7580`) unconditionally calls `CUITrigger::CUITrigger` (`+0xA56430`) and binds ownership to `CInterface + 0x398`. (2) `.material` samplers specifying `$flat.tga` / `$white.tga` fall through to `CFileManager::GetSystemPath` (`+0x896190`), which rejects `$` as an invalid path prefix across every VFS mount. |
| **Fix / Workaround** | (1) In `.package` files shared across multiple `.interface` roots, replace XML `<trigger name="..."/>` tags with a hidden `<panel visible="false" noclick="true" onloadlua="if UITrigger and UITrigger.CreateTrigger then UITrigger.CreateTrigger('...') end" />`, and guard `SetSave('<cvar>', ...)` with `If(!VarExists('<cvar>'), CreateString('<cvar>', ...))`. (2) Normalize procedural sampler paths in `.material` files from `$flat.tga` / `$white.tga` to bare `$flat` / `$white`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```
