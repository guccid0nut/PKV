---
id: FN-022
kind: engine-law
title: "Dynamic ModelPanel SetModel Resets Clip to Idle Requiring Post-SetModel SetAnim"
binary_or_subsystem: "override/ui | k2_x64.dll"
rvas: ["0x000000"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-09
tags: [k2, ui, modelpanel, portrait, setmodel, setanim, game_new]
---

# FN-022: Dynamic ModelPanel SetModel Resets Clip to Idle Requiring Post-SetModel SetAnim

## Summary
In K2 UI (`CModelPanel`), declaring `onloadlua="self:SetAnim('portrait')"` on a `<modelpanel>` with `model=""` (such as `game_center_portrait_model` in `game_apex.interface` and `game_legacy.interface`) is a no-op at interface load time because no `.mdf` skeleton is loaded yet. When Lua subsequently assigns a dynamic hero/avatar model via `portraitModel:SetModel(model)`, `CModelPanel::SetModel` resets the active animation channel to the modelpanel's `anim` property (`idle` / `default_1.clip`), causing heroes and Alt Avatars whose `idle` pose lies outside the close-up `portrait` camera frustum (`campitch="-15" camdist="34"`) to render an empty cosmic orb.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Bottom-center 3D animated hero portrait (`game_center_portrait_model`) displays only the purple cosmic background sphere (`portrait_bg.tga`) with no visible hero head/bust for heroes and Alt Avatars whose `idle` stance differs from their `portrait.clip` camera framing. |
| **Root Cause** | Commit `eb256c36d9` switched `game_center_portrait_model` from XML `watch="ActiveModel" ontrigger="SetModel(param); SetAnim('portrait');"` to a Lua `RegisterWatch('ActiveModel', ...)` handler in `game_new.lua` that called `portraitModel:SetModel(model)` without calling `portraitModel:SetAnim('portrait')`, relying on `onloadlua="self:SetAnim('portrait')"` which ran when `model=""`. |
| **Fix / Workaround** | Immediately follow `portraitModel:SetModel(model)` with `if portraitModel.SetAnim then portraitModel:SetAnim('portrait') end` inside the `ActiveModel` watch callback across all 5 parity mirrors of `ui/scripts/game_new.lua`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_client_lua.ps1 -Path games/client/override/ui/scripts/game_new.lua
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Whenever a K2 `<modelpanel>` uses a non-`idle` clip (such as `portrait`) and loads models dynamically via `widget:SetModel(path)` in Lua, always invoke `widget:SetAnim('<clip>')` immediately after every `SetModel` call rather than relying on `onloadlua`.
