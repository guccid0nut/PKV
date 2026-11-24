---
id: FN-001
kind: crash-triage
title: "OBS graphics-hook64.dll Thread 29 WndProc Race Against CLuaScriptManager::PushCallback"
binary_or_subsystem: "k2_x64.dll | version.dll (NativeGuard)"
rvas: ["0x92E9A6", "0x92BB66", "0xCC0A90"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [k2, wndproc, obs, threading, lua, native-guard, crash]
---

# FN-001: OBS `graphics-hook64.dll` Thread 29 `WndProc` Race Against `CLuaScriptManager::PushCallback`

## Summary
External capture overlays such as OBS (`graphics-hook64.dll`) inject background threads (e.g. Thread 29) into `hon_x64.exe` and invoke the subclassed window procedure (`KongorSubclassWndProc`) off the main engine thread. Executing `CConsole::Execute` or Lua callbacks synchronously from that background thread corrupts the single-threaded Lua 5.1 state (`lua_State*`) while Thread 0 is inside `CLuaScriptManager::PushCallback` (`k2_x64.dll + 0x92E9A6` / `0x92BB66`), causing an immediate `0xC0000005` access violation in `luaH_getstr` (`k2_x64.dll + 0xCC0A90`).

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | `0xC0000005` crash in `k2_x64.dll` (`+0x92E9A6`, `+0x92BB66`, or `+0xCC0A90`) during IPC commands or window messages while OBS or an external overlay hook is loaded. |
| **Root Cause** | `KongorSubclassWndProc` or IPC handlers executed `CConsole::Execute` off Thread 0 when `GetCurrentThreadId() != GetWindowThreadProcessId(hWnd, NULL)`, racing Thread 0's `lua_State*` stack and table pointers. |
| **Fix / Workaround** | Verify `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` at the top of `KongorSubclassWndProc`; if called off-thread, re-post via `PostMessageW(hWnd, msg, wParam, lParam)` and queue console commands to the main-thread ring buffer via `CConsole::AddCmdBuffer`. |

## Verification Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Never call `CConsole::Execute` or touch `lua_State*` synchronously from a `WndProc` or Named Pipe IPC thread without first verifying `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` and queuing via `CConsole::AddCmdBuffer`.
