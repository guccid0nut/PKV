---
id: MCP-178
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "CAPTURE-TIMEOUT (MCP-2026-10-05)"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "Always run `hon_x64` with borderless windowed flags (`+Set vid_fullscreen false +Set vid_borderless true`) for headless/offscreen captures. Avoid indefinite blocking in `kongor_capture_window` by enforcing a 15-second cancellation token ins..."
---

# MCP-178: CAPTURE-TIMEOUT (MCP-2026-10-05)

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Return capture PNG or error within standard timeout (under 30s). |
| **Actual (Symptom)** | Tool execution timed out after 3 minutes (180s) with `context deadline exceeded`. |
| **Root Cause** | When the target game window is in exclusive fullscreen mode or unfocused DX9 state without swapchain presentation, the transient capture worker or Vulkan hook hangs indefinitely waiting for a frame acquire or focus change. |
| **Fix / Remediation** | Always run `hon_x64` with borderless windowed flags (`+Set vid_fullscreen false +Set vid_borderless true`) for headless/offscreen captures. Avoid indefinite blocking in `kongor_capture_window` by enforcing a 15-second cancellation token inside the capture pipeline. Use `scratch/capture_hon.ps1` when quick diagnostic verification is needed. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"target":"hon_x64","capture_method":"transient","steal_focus":true}`
* **Expected**: Return capture PNG or error within standard timeout (under 30s).
* **Actual**: Tool execution timed out after 3 minutes (180s) with `context deadline exceeded`.
* **Root Cause**: When the target game window is in exclusive fullscreen mode or unfocused DX9 state without swapchain presentation, the transient capture worker or Vulkan hook hangs indefinitely waiting for a frame acquire or focus change.
* **Status**: `[RESOLVED]` (workaround via direct Win32 desktop-attached capture script `scratch/capture_hon.ps1` with 5s timeout).
* **Next-Time Prevention Rule**: Always run `hon_x64` with borderless windowed flags (`+Set vid_fullscreen false +Set vid_borderless true`) for headless/offscreen captures. Avoid indefinite blocking in `kongor_capture_window` by enforcing a 15-second cancellation token inside the capture pipeline. Use `scratch/capture_hon.ps1` when quick diagnostic verification is needed.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Always run `hon_x64` with borderless windowed flags (`+Set vid_fullscreen false +Set vid_borderless true`) for headless/offscreen captures. Avoid indefinite blocking in `kongor_capture_window` by enforcing a 15-second cancellation token ins...
