---
id: FN-007
kind: crash-triage
title: "version.dll DllMain Spawns Detached Threads and Hooks kernelbase.dll (Never LoadLibrary/FreeLibrary in Test Runners)"
binary_or_subsystem: "games/client/version.dll (NativeGuard)"
rvas: ["0x01CEE0", "0x01DE60"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [native-guard, version-dll, pe64, netcode, crash-triage]
---

# FN-007: version.dll DllMain Spawns Detached Threads and Hooks kernelbase.dll

## Summary
Calling `LoadLibraryW("games/client/version.dll")` followed by `FreeLibrary` inside a standalone verification script (`pwsh.exe` or `dotnet test`) executes `DllMain(DLL_PROCESS_ATTACH)` $\to$ `InstallNativeGuard()`, which hooks `kernelbase.dll`, injects `mimalloc-override.dll`, opens `\\.\pipe\kongor-native-client`, and spawns 6 detached watcher threads (`K2SafeHooksWatcherWorker`, `NativeGuardWindowHookWatcher`, etc.). When `FreeLibrary` unmaps `version.dll`, the detached threads wake up from `Sleep(5)` in unmapped memory and crash the process with `0xC0000409` (`-1073740791`).

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Process stalls for ~17s, writes stray `logs/crash/native_guard_sentinel.log` relative to CWD, and exits with `-1073740791` (`0xC0000409`) immediately after `FreeLibrary(hMod)`. |
| **Root Cause** | `native/proxies/native_guard/dllmain.cpp:3992-4249` (`InstallNativeGuard`) spawns detached watcher threads and installs inline hooks in `kernelbase.dll` that are not torn down on `DLL_PROCESS_DETACH`. |
| **Fix / Workaround** | Inspect `games/client/version.dll` via `Get-KongorPe64Exports` in `scripts/lib/MutantHarness.psm1` (which parses the PE64 `IMAGE_DIRECTORY_ENTRY_EXPORT` table and maps `FunctionOffsets` directly to `.text` file offsets without invoking `LoadLibraryW`). |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_netcode_oracle.ps1
```

## Prevention Invariant
Never call `LoadLibraryW` on `games/client/version.dll` inside an offline verification script or test runner. Always inspect exports and `.text` machine code via `Get-KongorPe64Exports`.
