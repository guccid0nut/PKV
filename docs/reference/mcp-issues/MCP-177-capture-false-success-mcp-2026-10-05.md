---
id: MCP-177
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "CAPTURE-FALSE-SUCCESS (MCP-2026-10-05)"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "Never accept a capture as proof without viewing it. Treat `transient_desktop_dc` results as unverified, and retry if the frame lacks the HoN client chrome. Tool fix: after the transient copy, compare against a `print_window`/DWM thumbnail f..."
---

# MCP-177: CAPTURE-FALSE-SUCCESS (MCP-2026-10-05)

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | PNG of the `hon_x64` game surface. |
| **Actual (Symptom)** | Returned `status: success`, `hwnd: 0x008C0BC0`, `windowTitle: "Heroes of Newerth x64"`, `captureMethod: transient_desktop_dc`, but the PNG showed **Windows File Explorer** (the user's foreground window). The anomaly detector then reported a false "Utah Teapot fallback" on the Explorer image. Separat... |
| **Root Cause** | - |
| **Fix / Remediation** | Never accept a capture as proof without viewing it. Treat `transient_desktop_dc` results as unverified, and retry if the frame lacks the HoN client chrome. Tool fix: after the transient copy, compare against a `print_window`/DWM thumbnail fingerprint, or confirm `GetForegroundWindow()==hwnd` before blitting. Return `status: unverified` otherwise. T... |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"target":"pid:91264","capture_method":"transient","steal_focus":true,"detect_anomalies":true}`
* **Expected**: PNG of the `hon_x64` game surface.
* **Actual**: Returned `status: success`, `hwnd: 0x008C0BC0`, `windowTitle: "Heroes of Newerth x64"`, `captureMethod: transient_desktop_dc`, but the PNG showed **Windows File Explorer** (the user's foreground window). The anomaly detector then reported a false "Utah Teapot fallback" on the Explorer image. Separately, `capture_method: print_window` returns a **blank white** frame for the D3D9 client.
* **Root Cause (suspected)**: The transient path falls back to a desktop-DC copy of the window rect. When Windows foreground-lock refuses `SetForegroundWindow` (user actively using the desktop), whatever window is on top gets captured. No post-capture verification checks that the pixels belong to the target.
* **Status**: `[OPEN]`. An immediate retry succeeded (real game frame).
* **Next-Time Prevention Rule**: Never accept a capture as proof without viewing it. Treat `transient_desktop_dc` results as unverified, and retry if the frame lacks the HoN client chrome. Tool fix: after the transient copy, compare against a `print_window`/DWM thumbnail fingerprint, or confirm `GetForegroundWindow()==hwnd` before blitting. Return `status: unverified` otherwise. The anomaly detector must not run on unverified frames.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Never accept a capture as proof without viewing it. Treat `transient_desktop_dc` results as unverified, and retry if the frame lacks the HoN client chrome. Tool fix: after the transient copy, compare against a `print_window`/DWM thumbnail f...
