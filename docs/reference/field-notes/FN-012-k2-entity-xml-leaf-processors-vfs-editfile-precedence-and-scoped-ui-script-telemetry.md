---
id: FN-012
kind: engine-law
title: "K2 Entity XML Leaf Processors, VFS Editfile Precedence, and Scoped UI Script Telemetry"
binary_or_subsystem: "k2_x64.dll | cgame_x64.dll | game_shared_x64.dll | Kongor.Modding"
rvas: ["0xf50740", "0xf65030", "0xf68360", "0x263eb8", "0x656408", "0x6656c8"]
status: verified
provenance: Session
evidence_source: "src/extensions/modding/Core/Packaging/S2zArchiveReader.cs"
evidence_quote: "ClientModMountSpec.PureCompatibilityMountTiers"
supersedes: []
invalidated_by: ""
date: 2026-10-26
tags: [k2, entity, xml, vfs, modding, hot-reload, cvar, telemetry]
---

# FN-012: K2 Entity XML Leaf Processors, VFS Editfile Precedence, and Scoped UI Script Telemetry

## Summary
Cross-verification of archived Frostburn/S2 `CXMLProcessor` specifications against retail x64 binaries (`k2_x64.dll`, `game/cgame_x64.dll`, `game/game_shared_x64.dll`), `7,289` effective `.entity` archives/overrides, and live `hon_x64.exe` console introspection (`cmdlist` / `uicmdlist` / `cvarlist`) established four critical engine invariants:
1. **`game_shared_x64.dll` Leaf vs. Branch `.entity` XML Processors:** `320` non-root combat action tags (e.g. `attackaction` at `game_shared_x64.dll + 0x656408`) are strict leaf processors (`END_XML_PROCESSOR_NO_CHILDREN`) that silently discard nested XML children, while `25` legacy forum tags (`onsubscriberunitdeath`, `settooltipnumparam`, `onremove`, `toggledaynight`, etc.) are absent from our x64 binary and silently no-op.
2. **Real K2 Reload Commands vs. Non-Existent `ReloadEntity` / `res_checkModified`:** Neither `ReloadEntity` nor `ReloadMaster` exists in K2 x64 (`cmdlist reload` + PE UTF-16LE scan). Real console reloads are `ReloadRes` (`k2_x64.dll + 0xf50740`), `ReloadInterfaces`, `ReloadInterface <name>`, `ReloadSelected` (`cgame_x64.dll + 0x263eb8`), `ReloadMaterials`, `ReloadModels`, and `ReloadTextures`, plus UI-only `UICmd("ReloadFile('<vpath>')")` (`k2_x64.dll + 0xf65030`). Passing `+Set res_checkModified false` creates an unread dynamic `[S A]` string CVar (`^wA ^*] ^rstring res_checkModified = "false"`).
3. **Scoped C++ UI Script Telemetry & Entity Debug Lines:** `ui_debugScriptInterface` (`k2_x64.dll + 0xf68360`) and `ui_debugScriptWidget` filter `ui_debugScript` traces inside C++ before console emission, avoiding global tick-spam. In-world `<drawdebugline />` and `<drawdebugx />` actions render when `cg_drawDebugLines 1` (`game_shared_x64.dll + 0x6656c8`) is enabled (`vid_drawModelBounds` / `cg_drawNavGridUnits` replace 32-bit `d_drawUnitBounds`).
4. **Mod `<editfile>` VFS Base Precedence Alignment:** `GameResourceResolver.CreateForClient` (`src/extensions/modding/Core/Packaging/S2zArchiveReader.cs`) now resolves `<editfile>` base content across the full client mount precedence (`base` -> `game` -> `ClientPackageFiles` -> `../shared` -> `KONGOR/configuration` -> `KONGOR/updates` -> `KONGOR/extensions`) while skipping `resources999.s2z` (`ModArchiveName`), preventing stale patches against shadowed `resources0.s2z` entries when `KONGOR/updates/*.s2z` or `ClientPackageFiles` owns the active file.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) Nested `<damage>` inside leaf `<attackaction>` or legacy `<onremove>` in `.entity` files silently fails at runtime with zero console error. (2) `.honmod` `<editfile>` patches apply against stale `game/resources0.s2z` files instead of active `ClientPackageFiles/` or `KONGOR/updates/*.s2z` overrides, or match inside Lua `--` comments. (3) Enabling `ui_debugScript true` floods `console.log` across every HUD tick, and `+Set res_checkModified false` / `ReloadEntity` silently no-ops. |
| **Root Cause** | (1) `CXMLProcessor` leaf macros in `game_shared_x64.dll` never iterate child XML nodes, and unlinked tags fall through processor dispatch. (2) `GameResourceResolver` (`S2zArchiveReader.cs`) previously scanned only `game/` (`resources0..99.s2z`) and `FuzzyStringPatcher` lacked Lua comment-span awareness. (3) `ReloadEntity` and `res_checkModified` do not exist in K2 x64 PE `.rdata` tables; `ReloadRes` (`0xf50740`), `ReloadSelected` (`0x263eb8`), and `UICmd("ReloadFile")` (`0xf65030`) are the actual registered commands. |
| **Fix / Workaround** | (1) Enforced `[K2-ENTITY-LEAF]` (`320` strict leaf actions) and `[K2-ENTITY-ABSENT-TAG]` (`25` absent tags) in `scripts/verify_xml_syntax.ps1` and `scripts/gate_check.ps1` Tier 3 (`docs/reference/k2_entity_xml_schema.json`). (2) Aligned `GameResourceResolver.CreateForClient` with `ClientModMountSpec` and added Lua comment-aware matching + syntax verification in `FuzzyStringPatcher` / `ModManager`. (3) Added `-DebugUiScriptInterface`, `-DebugUiScriptWidget`, `-DebugHoverWidget`, and `-DynamicResReload` to `scripts/launch_screen_practice.ps1` and built `scripts/sync_k2_engine_catalogs.ps1`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/sync_k2_engine_catalogs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1
dotnet test tests/unit/extensions/Kongor.Modding.Tests/Kongor.Modding.Tests.csproj --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
- Always validate `.entity`, `.package`, `.interface`, and `.effect` modifications through `scripts/verify_xml_syntax.ps1` (and Tier 3 `scripts/gate_check.ps1`) before hot-reloading so leaf-action nesting, absent tags, multiline `--` `on*lua` comments, and NPOT `icon*.dds` textures fail fast.
- Never reference `ReloadEntity`, `ReloadMaster`, `res_checkModified`, or 32-bit `d_drawUnitBounds`; verify candidate K2 commands and CVars against PE binaries and live `hon_x64.exe` via `scripts/sync_k2_engine_catalogs.ps1`.
