---
id: MCP-061
server: kongor
tool: kongor_capture_window
category: ipc-timeout
title: "Failed to Acquire Vulkan Swapchain Frame on D3D9 Headless Client"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/CaptureWindowHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-09-25
last_seen: 2026-11-04
tags: [kongor, kongor_capture_window, ipc-timeout]
prevention_rule: "Verify `vid_driver`; when client runs `d3d9`, use `kongor_transient_capture` or Vulkan driver."
---

# MCP-061: Failed to Acquire Vulkan Swapchain Frame on D3D9 Headless Client

## Summary
Tracked MCP issue on `kongor` / `kongor_capture_window` (category: `ipc-timeout`, status: `documented`, occurrences: 3). Acquire Vulkan swapchain frame from shared memory for PID 30036. -> Failed with error: `Failed to acquire Vulkan swapchain frame from shared memory for PID 30036 (timeout or inactive layer).`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Acquire Vulkan swapchain frame from shared memory for PID 30036. |
| **Actual (Symptom)** | Failed with error: `Failed to acquire Vulkan swapchain frame from shared memory for PID 30036 (timeout or inactive layer).` |
| **Root Cause** | The game client PID 30036 was launched with `+Set vid_driver d3d9`. Because the client is running Direct3D9 rather than Vulkan, the Vulkan replay capture layer (`VK_LAYER_HON_replay_capture`) is not active in this process, making shared memory ring buffers unavailable. |
| **Fix / Remediation** | Cascaded to `kongor_transient_capture` for D3D9 offscreen capture. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "capture_method": "vulkan_offscreen"
  }
  

Logs:

  {
    "status": "error",
    "target": "hon_x64",
    "pid": 30036,
    "errorMessage": "Failed to acquire Vulkan swapchain frame from shared memory for PID 30036 (timeout or inactive layer)."
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Verify `vid_driver`; when client runs `d3d9`, use `kongor_transient_capture` or Vulkan driver.
