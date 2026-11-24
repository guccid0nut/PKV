---
id: MCP-015
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "Vulkan Offscreen Capture Shared Memory Acquire Timeout After Disconnect"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "Allow dwell delay and use `capture_method: 'auto'` after heavy state transitions."
---

# MCP-015: Vulkan Offscreen Capture Shared Memory Acquire Timeout After Disconnect

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Retrieve Vulkan swapchain frame from shared memory ring buffer. |
| **Actual (Symptom)** | Failed with `Failed to acquire Vulkan swapchain frame from shared memory for PID 27536 (timeout or inactive layer).` |
| **Root Cause** | Right after executing a heavy engine state reset (`Disconnect`), the client engine pauses rendering / swapchain presentation for several frames while clearing network buffers, causing the Win32 `EventWaitHandle` (`HonFrameReady_{pid}_{slot}`) to exceed the capture wait timeout. |
| **Fix / Remediation** | Added dwell delay and fallback handling when capturing post-disconnect state frames. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "vulkan_offscreen",
    "target": "hon_x64",
    "custom_output_path": "<ArtifactsDir>\\screen_after_disconnect.png",
    "steal_focus": false,
    "detect_anomalies": false
  }
  

Logs:

  Failed to acquire Vulkan swapchain frame from shared memory for PID 27536 (timeout or inactive layer).
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Allow dwell delay and use `capture_method: 'auto'` after heavy state transitions.
