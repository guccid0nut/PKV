---
id: MCP-192
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "KONGOR-CAPTURE-WINDOW-TIMEOUT (MCP-2026-10-06)"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "For foreground D3D9 client instances, use PowerShell Win32 `BitBlt` capture script or specify `capture_method: 'window_dc'` / `capture_method: 'print_window'`."
---

# MCP-192: KONGOR-CAPTURE-WINDOW-TIMEOUT (MCP-2026-10-06)

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Visual capture returned within 5 seconds. |
| **Actual (Symptom)** | MCP tool call timed out after 3m0s with context deadline exceeded. |
| **Root Cause** | `capture_method: "auto"` defaulted to Vulkan offscreen shared memory / direct GPU DMA, but `hon_x64` was launched in foreground D3D9 mode, causing DMA wait to block indefinitely. |
| **Fix / Remediation** | For foreground D3D9 client instances, use PowerShell Win32 `BitBlt` capture script or specify `capture_method: "window_dc"` / `capture_method: "print_window"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"capture_method": "auto", "target": "hon_x64"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
For foreground D3D9 client instances, use PowerShell Win32 `BitBlt` capture script or specify `capture_method: 'window_dc'` / `capture_method: 'print_window'`.
