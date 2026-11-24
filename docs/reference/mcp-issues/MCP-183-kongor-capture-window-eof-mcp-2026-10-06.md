---
id: MCP-183
server: kongor
tool: kongor_capture_window
category: transport-eof
title: "KONGOR-CAPTURE-WINDOW-EOF (MCP-2026-10-06)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-06
last_seen: 2026-10-24
tags: [kongor, kongor_capture_window, transport-eof]
prevention_rule: "Use direct script capture (`scratch/capture_current_hon.py` or `scratch/capture_vulkan_frame.py`) when `kongor` MCP stdio transport is disconnected."
---

# MCP-183: KONGOR-CAPTURE-WINDOW-EOF (MCP-2026-10-06)

## Summary
Tracked MCP issue on `kongor` / `kongor_capture_window` (category: `transport-eof`, status: `documented`, occurrences: 2). Capture frame via MCP tool. -> Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Capture frame via MCP tool. |
| **Actual (Symptom)** | Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The stdio transport for the `kongor` MCP server was closed / not restarted. |
| **Fix / Remediation** | Use direct script capture (`scratch/capture_current_hon.py` or `scratch/capture_vulkan_frame.py`) when `kongor` MCP stdio transport is disconnected. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"target":"hon_x64","save_artifact":true,"custom_output_path":"...","detect_anomalies":true}`
* **Expected**: Capture frame via MCP tool.
* **Actual**: Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.
* **Root Cause**: The stdio transport for the `kongor` MCP server was closed / not restarted.
* **Status**: `[RESOLVED]` (Falling back to direct script capture via Vulkan swapchain layer or GDI BitBlt).
* **Next-Time Prevention Rule**: Use direct script capture (`scratch/capture_current_hon.py` or `scratch/capture_vulkan_frame.py`) when `kongor` MCP stdio transport is disconnected.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Use direct script capture (`scratch/capture_current_hon.py` or `scratch/capture_vulkan_frame.py`) when `kongor` MCP stdio transport is disconnected.
