---
id: MCP-010
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "Vulkan Offscreen Capture Timeout & Desktop DC Black Fallback"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "Ensure `VK_INSTANCE_LAYERS=VK_LAYER_HON_replay_capture`; use `kongor_transient_capture` if offscreen."
---

# MCP-010: Vulkan Offscreen Capture Timeout & Desktop DC Black Fallback

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Tier 0 direct Vulkan swapchain frame grab via shared memory `Local\HonReplayFrameBuffer_{pid}` with 0ms dwell time and 0% window exposure. |
| **Actual (Symptom)** | Failed with `Failed to acquire Vulkan swapchain frame from shared memory for PID 31152 (timeout or inactive layer)`. When called with `capture_method: "auto"`, it silently fell back to `desktop_dc`, capturing blank/dark desktop coordinates because the game window is parked offscreen at `(-32000, -32... |
| **Root Cause** | The game client process `hon_x64.exe` (PID 31152) was launched without the Vulkan capture layer (`VK_INSTANCE_LAYERS=VK_LAYER_HON_replay_capture`, `HON_CAPTURE_ENABLE=1`), or the Vulkan layer did not initialize shared memory because rendering was paused/idle in background. - When the layer is absent, `capture_method: "auto"` cascades down to Tier 2... |
| **Fix / Remediation** | Documented fallback to `kongor_transient_capture` or restarting client with Vulkan capture layer enabled. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "vulkan_offscreen",
    "target": "hon_x64"
  }
  

Logs:

  {
    "status": "error",
    "target": "hon_x64",
    "pid": 31152,
    "errorMessage": "Failed to acquire Vulkan swapchain frame from shared memory for PID 31152 (timeout or inactive layer)."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Ensure `VK_INSTANCE_LAYERS=VK_LAYER_HON_replay_capture`; use `kongor_transient_capture` if offscreen.
