---
id: FN-010
kind: engine-law
title: "Dedicated Server version.dll Missing Import, System32 Basename Collision, and Per-Port Boot Sentinel Isolation"
binary_or_subsystem: "hon_x64.exe (-dedicated) | version.dll (NativeGuard) | COMPEL"
rvas: ["0x01CEE0"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-26
tags: [k2, dedicated-server, compel, native-guard, version-dll, injection, boot-sentinel, named-pipe]
---

# FN-010: Dedicated Server version.dll Missing Import, System32 Basename Collision, and Per-Port Boot Sentinel Isolation

## Summary
Unlike the Windows game client (`hon_x64.exe` loading `vid_d3d9_x64.dll` / `d3d9.dll`, which imports `version.dll`), `hon_x64.exe -dedicated` and `k2_x64.dll` do **not** import `version.dll`. Assuming CWD DLL side-loading loads `NativeGuard` (`version.dll`) on dedicated servers leaves `\\.\pipe\hon-server-native-{Port}` and `KongorTelemetry_{Port}` uncreated while `stdin` console pipes remain unread by K2. Furthermore, checking remote process modules by basename (`"version.dll"`) false-matches `C:\Windows\System32\version.dll` loaded transitively by WinSock/secur32, and spawning multiple workers in a shared CWD (`games/hon server/`) without per-port boot sentinels causes concurrent workers to trip `GEODE-SAFE-MODE (reason=BOOT_SENTINEL_UNCLEARED)`.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | (1) COMPEL workers remain in `"Starting"` telemetry state, `\\.\pipe\hon-server-native-{Port}` does not exist, and `AddFakePlayer` / console commands fall back to unread `stdin` (`200 OK` with zero engine execution). (2) Basename module check reports `version.dll` already loaded when only `System32\version.dll` is mapped. (3) Concurrent worker ports (`11235` + `11236`) sharing CWD trip `BOOT_SENTINEL_UNCLEARED` safe-mode on boot. |
| **Root Cause** | (1) PE import table of `hon_x64.exe` and `k2_x64.dll` has zero imports from `version.dll`; only the client graphics stack (`vid_d3d9_x64.dll` / DXVK `d3d9.dll`) triggers Windows loader side-loading of `version.dll`. (2) `NativeProcessInjectionService.GetRemoteModuleHandle` matched module basenames rather than full filesystem paths (`GetModuleFileNameExW`). (3) `runtime_patch_controller.cpp` defaults its boot crash sentinel to `userdata/logs/native_guard_boot.sentinel` relative to CWD unless overridden via `KONGOR_BOOT_SENTINEL_PATH`. |
| **Fix / Workaround** | (1) Explicitly inject `<serverDir>\version.dll` into Windows `-dedicated` workers via `NativeProcessInjectionService.InjectDll` and verify mapping with `GetRemoteModuleHandleByFullPath` (`Path.GetFullPath` comparison against `GetModuleFileNameExW`). (2) Set `KONGOR_BOOT_SENTINEL_PATH = "userdata/logs/native_guard_boot_{port}.sentinel"` per worker process so concurrent boots never collide. (3) Route worker commands via `DedicatedServerWorker.WriteCommandAsync` (`0x4D 0x57` `OpCode 0x0003` -> `0x0004` JSON ACK) and guard `OnWorkerExited` on clean drains (`exitCode == 0` / `Restarting`) so graceful `quit` never increments `CrashCount`. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
dotnet test tests/unit/apps/COMPEL.Tests/COMPEL.Tests.csproj -c Debug --nologo
```

## Prevention Invariant
Never assume `hon_x64.exe -dedicated` auto-loads `version.dll` from CWD, never verify injected proxy DLLs by basename when a `System32` namesake exists, and always isolate `KONGOR_BOOT_SENTINEL_PATH` per worker port when multiple K2 instances share a working directory.
