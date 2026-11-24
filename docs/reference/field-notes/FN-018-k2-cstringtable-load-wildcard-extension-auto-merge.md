---
id: FN-018
kind: engine-law
title: "K2 CStringTable::Load Wildcard Extension Auto-Merge (<stem>*_<lang>.str)"
binary_or_subsystem: "k2_x64.dll | override/stringtables"
rvas: ["0x9ED7B0", "0x9EE1C0"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-11-05
provenance: "Session"
evidence_source: "games/client/override/stringtables/interface_reborn_en.str"
evidence_quote: "map_midwars_halloween						Mid Wars (Halloween)"
tags: [cstringtable, stringtables, interface_reborn_en, localization, vfs]
---

# FN-018: K2 `CStringTable::Load` Wildcard Extension Auto-Merge (`<stem>*_<lang>.str`)

## Summary
Static decompilation of `CStringTable::Load` (`FUN_1809ed7b0`, RVA `0x9ED7B0` in `k2_x64.dll`) paired with live `hon_x64.exe` `Translate(...)` verification establishes how K2 merges additive localized stringtables without duplicating the 1.08 MB base `interface_en.str`:

1. When K2 loads a localized stringtable such as `/stringtables/interface_en.str` (localized bit `(*(uint*)(this + 0x68) >> 4) & 1 != 0`), it logs `Loading StringTable '/stringtables/interface_en.str'` once and parses the primary buffer via `FUN_1809ee1c0` (`0x9EE1C0`).
2. Immediately afterward, `FUN_1809ed7b0` strips the base stem (`/stringtables/interface`), extracts the language suffix (`_en.str`), constructs the wildcard pattern `interface*_en.str`, and invokes `CFileManager::GetFileList("/stringtables/", "interface*_en.str", false, ...)`.
3. Every matching file across the VFS priority stack (e.g., `stringtables/interface_reborn_en.str` in `override/` > `ClientPackageFiles/` > `game/` > `resources*.s2z`) other than the base `interface_en.str` itself is opened via `CFileManager::GetFile` and merged into the same `CStringTable` via `FUN_1809ee1c0` **silently** (no secondary `Loading StringTable` line is emitted to `console.log`).

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | `console.log` only prints `Loading StringTable '/stringtables/interface_en.str'` and `Loading StringTable '/stringtables/interface.str'`, making `stringtables/interface_reborn_en.str` appear unmounted when auditing logs, or tempting authors to copy the entire 1.08 MB `interface_en.str` into `games/client/override/stringtables/`. |
| **Root Cause** | `CStringTable::Load` (`k2_x64.dll + 0x9ED7B0`) only logs the base stringtable path at entry, then silently enumerates and merges all `<stem>*_<lang>.str` companions via `CFileManager::GetFileList`. |
| **Fix / Workaround** | Place additive UI and custom-map English localization keys in `games/client/override/stringtables/interface_reborn_en.str` (mirrored to `ClientPackageFiles/stringtables/interface_reborn_en.str` and `games/client/game/stringtables/interface_reborn_en.str` for Tier 5 parity) rather than overriding the 18,430-line `interface_en.str`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```
