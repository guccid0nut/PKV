---
id: MCP-048
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "Vulkan Offscreen Capture Memory Acquisition Timeout on Inactive Layer"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Services/WindowCapturer.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: ["MCP-010", "MCP-015", "MCP-039", "MCP-177", "MCP-178", "MCP-192", "MCP-137"]
occurrences: 12
date: 2026-09-25
last_seen: 2026-11-05
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "`WindowCapturer` self-heals by recovering cached frames or cascading to `PrintWindow`."
---

# MCP-048: Vulkan Offscreen Capture Memory Acquisition Timeout on Inactive Layer

## Summary
Tracked MCP issue on `kongor` / `kongor_capture_window` (category: `window-resolution`, status: `investigating`, occurrences: 12). Acquire frame directly from Vulkan swapchain shared memory ring buffer for PID 55852. -> Failed with `Failed to acquire Vulkan swapchain frame from shared memory for PID 55852 (timeout or inactive layer).`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Acquire frame directly from Vulkan swapchain shared memory ring buffer for PID 55852. |
| **Actual (Symptom)** | Failed with `Failed to acquire Vulkan swapchain frame from shared memory for PID 55852 (timeout or inactive layer).` |
| **Root Cause** | Direct Vulkan swapchain shared memory frame acquisition requires the client to actively present frames through `VK_LAYER_HON_replay_capture`. When the window is parked offscreen at `(-32000, -32000)` and running without active focus, K2 engine throttles background rendering (`vid_bkgndSleep`, `cl_sleep`) or halts presentation when the scene is stat... |
| **Fix / Remediation** | **Cached Frame Recovery on Timeout**: `VulkanMemoryMappedFrameGrabber.GrabSingleFrameFromMapAsync` now probes all ring buffer slots if `WaitHandle.WaitAny` times out. If any slot holds a valid `FrameHeader` from a previous presentation, it immediately recovers the most recent frame (highest `FrameIndex`) with zero latency. 2. **Multi-Candidate Slot... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "vulkan_offscreen"
  }
  

Logs:

  Failed to acquire Vulkan swapchain frame from shared memory for PID 55852 (timeout or inactive layer).
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
`WindowCapturer` self-heals by recovering cached frames or cascading to `PrintWindow`.
