---
id: FN-008
kind: crash-triage
title: "Force-Killing Vulkan/DXVK/D3D9 Processes Mid-Frame Can Leave an Unkillable 1-Thread Windowless Kernel Zombie Holding .s2z Locks"
binary_or_subsystem: "hon_x64.exe | d3d9.dll (DXVK 3.0.2) | scripts/lib/Stop-KongorGameCleanly.ps1"
rvas: ["0x758FE0", "0x843880"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [dxvk, vulkan, d3d9, zombie-process, wddm, quit-before-kill, crash-triage]
---

# FN-008: Force-Killing Vulkan/DXVK/D3D9 Processes Mid-Frame Can Leave an Unkillable 1-Thread Windowless Kernel Zombie Holding .s2z Locks

## Summary
Calling `Stop-Process -Force` or `taskkill /F` directly on `hon_x64.exe` while the GPU thread is inside a kernel-mode WDDM (`dxgkrnl.sys`) ioctl wait during a D3D9/DXVK Vulkan `Present` or swapchain teardown kills all user-mode threads except the single thread blocked inside `dxgkrnl.sys`. The process becomes an unkillable 1-thread windowless zombie (`Threads.Count == 1`, `MainWindowHandle == 0`) that refuses `TerminateProcess`, holds exclusive file locks on `.s2z` archives, and blocks subsequent client launches until a Windows reboot.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | `Get-Process hon_x64` shows an active PID with `Threads.Count == 1` and `MainWindowHandle == 0`. `Stop-Process -Force` and `taskkill /F /PID <pid>` fail with `Access is denied` (`0x80004005`), and `.s2z` archives or `hon_x64.exe` remain locked on disk. |
| **Root Cause** | Windows kernel-mode WDDM driver (`dxgkrnl.sys`) marks the calling thread non-alertable during certain GPU fence/swapchain kernel transitions. Terminating the process via `TerminateProcess` before `CHost::Execute("quit")` / `WM_CLOSE` tears down the swapchain traps that single thread in kernel space until IRP completion (which never arrives because the user-mode worker threads were terminated). |
| **Fix / Workaround** | Always use `Stop-KongorGameCleanly` (`scripts/lib/Stop-KongorGameCleanly.ps1`), which performs a 3-stage shutdown: (1) detects pre-existing 1-thread windowless zombies upfront, (2) requests graceful swapchain teardown via `\\.\pipe\kongor-native-client` (`cmd quit`) and `Process.CloseMainWindow()` (`WM_CLOSE`) with a `1500ms` settle window, and (3) falls back to `Stop-Process -Force` only if the process is still alive, followed by a post-kill 1-thread zombie check with actionable WDDM recovery diagnostics. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_zombie_guard.ps1
```

## Prevention Invariant
Never invoke raw `Stop-Process -Name hon_x64 -Force` in launcher or harness scripts without first attempting graceful teardown via `Stop-KongorGameCleanly`.
