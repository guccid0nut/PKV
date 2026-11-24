---
id: FN-002
kind: engine-law
title: "Lua 5.1 luaV_concat Allocator Exhaustion Under Per-Frame loadfile Polling"
binary_or_subsystem: "k2_x64.dll | override/ui/scripts"
rvas: ["0xCB6477"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [k2, lua, luav-concat, loadfile, memory, tao-req, crash]
---

# FN-002: Lua 5.1 `luaV_concat` Allocator Exhaustion Under Per-Frame `loadfile` Polling

## Summary
Polling external script files every frame inside a Lua UI tick handler (`onframe` / `registerWatch` calling `loadfile` or `dofile` unconditionally) fragments and exhausts K2's 32-bit-bounded Lua 5.1 allocator, resulting in a null-pointer allocation inside `luaV_concat` (`k2_x64.dll + 0xCB6477`) followed by an unguarded `memcpy(0x4c)` write crash (`0xC0000005`).

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Client crashes after several minutes of live runtime with `0xC0000005` at `k2_x64.dll + 0xCB6477` (`luaV_concat`, `memcpy` to destination `0x4c`). |
| **Root Cause** | Per-frame `loadfile` / string concatenation allocates transient Lua chunks faster than incremental GC collects them, causing the allocator to return `NULL` during string buffer growth in `luaV_concat`. |
| **Fix / Workaround** | Eliminate per-frame file polling; gate all script evaluation behind an event-driven CVar change signal (`tao_req` / `Cvar.GetCvar`) so `loadfile` only executes when a new sequence token is published. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_client_lua.ps1
```

## Prevention Invariant
Never call `loadfile`, `dofile`, or unbounded string concatenation inside per-frame Lua `onframe` callbacks; always gate dynamic evaluation on a CVar dirty-sequence check (`tao_req`) and keep per-tick handlers under $\le 1.0\text{ ms}$.
