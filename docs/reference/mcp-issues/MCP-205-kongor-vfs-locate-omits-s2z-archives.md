---
id: MCP-205
server: kongor
tool: kongor_vfs_locate
category: runtime-error
title: "KONGOR-VFS-LOCATE-OMITS-S2Z-ARCHIVES"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_vfs_locate, runtime-error]
prevention_rule: "For assets packed inside `.s2z` archives (`resources0.s2z`, `resources1.s2z`, `resources95.s2z`), inspect `.s2z` central directories directly using `[System.IO.Compression.ZipFile]::OpenRead(...)` or `scripts/read_archive_entry.ps1`."
---

# MCP-205: KONGOR-VFS-LOCATE-OMITS-S2Z-ARCHIVES

## Summary
Tracked MCP issue on `kongor` / `kongor_vfs_locate` (category: `runtime-error`, status: `documented`, occurrences: 1). Locate Beauty Empath (`Hero_Empath.Alt10`) `.mdf` and `.entity` files packed inside `games/client/game/resources*.s2z`. -> Returned `{"status": "not_found", "found": false}` because `kongor_vfs_locate` only scans loose filesystem directories (`override`, `base`, `game`, `ClientPackageFiles`, `shared`) and does not scan `.s2z` zip central directories.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_locate` |
| **Expected** | Locate Beauty Empath (`Hero_Empath.Alt10`) `.mdf` and `.entity` files packed inside `games/client/game/resources*.s2z`. |
| **Actual (Symptom)** | Returned `{"status": "not_found", "found": false}` because `kongor_vfs_locate` only scans loose filesystem directories (`override`, `base`, `game`, `ClientPackageFiles`, `shared`) and does not scan `.s2z` zip central directories. |
| **Root Cause** | `kongor_vfs_locate` checks loose directories on disk after loose game assets were quarantined into `.s2z` archives. |
| **Fix / Remediation** | For assets packed inside `.s2z` archives (`resources0.s2z`, `resources1.s2z`, `resources95.s2z`), inspect `.s2z` central directories directly using `[System.IO.Compression.ZipFile]::OpenRead(...)` or `scripts/read_archive_entry.ps1`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"vfs_path": "/heroes/empath/alt10/preview.mdf"}` and `{"vfs_path": "/heroes/empath/hero.entity"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_locate
```

## Prevention Invariant
For assets packed inside `.s2z` archives (`resources0.s2z`, `resources1.s2z`, `resources95.s2z`), inspect `.s2z` central directories directly using `[System.IO.Compression.ZipFile]::OpenRead(...)` or `scripts/read_archive_entry.ps1`.
