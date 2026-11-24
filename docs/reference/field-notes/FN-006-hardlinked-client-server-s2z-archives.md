---
id: FN-006
kind: vfs
title: "NTFS Hard-Link Sharing Across Client and Server .s2z Archives (resources0, resources5, resources98)"
binary_or_subsystem: "VFS | games/client/game/*.s2z | games/server/game/*.s2z"
rvas: ["0x000000"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [vfs, s2z, hardlink, ntfs, client, server, parity]
---

# FN-006: NTFS Hard-Link Sharing Across Client and Server `.s2z` Archives (`resources0`, `resources5`, `resources98`)

## Summary
Core archives `resources0.s2z`, `resources5.s2z`, and `resources98.s2z` are NTFS hard-linked between `games/client/game/` and `games/server/game/` to save disk space and guarantee client/server entity parity. Modifying one of these archives in-place without checking hard links either mutates both trees unexpectedly or breaks the hard link if replaced via temp-file rename while `hon_server.exe` or `hon_x64.exe` holds an open handle.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Unexpected server entity checksum drift, file-lock sharing violations during archive repacking, or silent divergence between `games/client/game/` and `games/server/game/`. |
| **Root Cause** | Shared NTFS inode across `games/client/game/resources{0,5,98}.s2z` and `games/server/game/resources{0,5,98}.s2z`. |
| **Fix / Workaround** | Inspect hard links first via `fsutil hardlink list <path>`, never mutate `resources0.s2z`, `resources5.s2z`, or `resources98.s2z` in-place without explicit user approval, and keep `override/` $\leftrightarrow$ `game/` / `server/` loose files synchronized via Tier 5 of `scripts/gate_check.ps1`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never modify `resources0.s2z`, `resources5.s2z`, or `resources98.s2z` in-place without running `fsutil hardlink list` and receiving explicit user approval; rely on `scripts/gate_check.ps1` Tier 5 to enforce loose-file parity across `override/`, `game/`, `verify_lua/`, and `server/`.
