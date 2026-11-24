---
id: MCP-039
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "Vulkan Offscreen Capture Timeout on Server Browser Transition"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "Use `capture_method: 'auto'` to allow fallback cascade during UI state transitions."
---

# MCP-039: Vulkan Offscreen Capture Timeout on Server Browser Transition

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Capture frame directly from Vulkan swapchain shared memory buffer `Local\HonReplayFrameBuffer_50544`. |
| **Actual (Symptom)** | Failed with error: `Failed to acquire Vulkan swapchain frame from shared memory for PID 50544 (timeout or inactive layer).` |
| **Root Cause** | The client was undergoing a dynamic UI transition (`FadeIn(150)` on server browser dialog), during which the swapchain frame presentation paused briefly, exceeding the 5-second wait handle timeout. Explicitly requesting `capture_method: "vulkan_offscreen"` disabled the automated multi-tier fallback cascade. |
| **Fix / Remediation** | Invoked `kongor_capture_window` with `capture_method: "auto"` allowing automated fallback cascade. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "vulkan_offscreen",
    "output_name": "client_server_browser_open",
    "process_name": "hon_x64"
  }
  

Logs:

  Failed to acquire Vulkan swapchain frame from shared memory for PID 50544 (timeout or inactive layer).
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Use `capture_method: 'auto'` to allow fallback cascade during UI state transitions.
