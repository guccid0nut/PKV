---
id: FN-005
kind: vfs
title: "K2 CArchive Signed 32-Bit Offset Overflow Above 1.5 GB and Mandatory allowZip64=False"
binary_or_subsystem: "k2_x64.dll | games/client/game/*.s2z"
rvas: ["0x000000"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [k2, s2z, vfs, zip64, archive, crash, 32bit-limit]
---

# FN-005: K2 `CArchive` Signed 32-Bit Offset Overflow Above 1.5 GB and Mandatory `allowZip64=False`

## Summary
K2's native zip reader (`CArchive` in `k2_x64.dll`) parses central directory offsets using signed 32-bit integers and rejects Zip64 extended headers. Any `.s2z` archive exceeding `1.5 GB` (safe ceiling `1,450 MB`, notably `resources94.s2z`) or written with `allowZip64 = True` triggers central-directory corruption or an immediate startup crash in `hon_x64.exe`.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | `hon_x64.exe` crashes on startup during archive mounting or silently fails to load assets from a newly repacked `.s2z` archive. |
| **Root Cause** | Archive size crossed the signed 32-bit offset boundary or Python/NET zip tooling emitted Zip64 extra fields (`0x0001`) that K2's parser cannot read. |
| **Fix / Workaround** | Keep every `.s2z` archive strictly under `1,450 MB`, set `allowZip64 = False` (or use `.NET` `ZipArchive` on `< 1.45 GB` shards), and route new overrides to `resources99.s2z` via `scripts/inject_override_to_s2z.ps1`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/inject_override_to_s2z.ps1 -RelativePath "ui/fe2/main_v2.interface" -DryRun
```

## Prevention Invariant
Never inject into `resources94.s2z`, never enable Zip64 on `.s2z` archives, and always route patch injections through `scripts/inject_override_to_s2z.ps1`.
