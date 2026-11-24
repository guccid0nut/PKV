---
id: MCP-137
server: kongor
tool: kongor_capture_window
category: window-resolution
title: "KONGOR-CAPTURE-WINDOW-TARGET-NOT-FOUND-AFTER-CONTROL-PANEL-CLOSE"
status: superseded
superseded_by: "MCP-048"
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [kongor, kongor_capture_window, window-resolution]
prevention_rule: "If `Target window not found` occurs during an interactive capture sequence, check `kongor_server_manage action: 'status'` and restart the cluster/client via `kongor_client_navigate` (`authMode: 'cluster'`, `ensureCluster: true`, `autoBoot:..."
---

# MCP-137: KONGOR-CAPTURE-WINDOW-TARGET-NOT-FOUND-AFTER-CONTROL-PANEL-CLOSE

## Summary
Repeat occurrence of [`MCP-048`] on `kongor` / `kongor_capture_window` (`window-resolution`). Superseded by `MCP-048`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Capture screenshot of `hon_x64.exe`. |
| **Actual (Symptom)** | Returned `{"status": "error", "errorMessage": "Target window not found for query 'hon_x64'. Make sure the target process or window is currently open."}`. |
| **Root Cause** | Closing the NEXUS Control Panel cleanly terminated the managed cluster and `hon_x64.exe` process just prior to the capture call. |
| **Fix / Remediation** | If `Target window not found` occurs during an interactive capture sequence, check `kongor_server_manage action: "status"` and restart the cluster/client via `kongor_client_navigate` (`authMode: "cluster"`, `ensureCluster: true`, `autoBoot: true`, `windowMode: "foreground"`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"capture_method": "auto", "custom_output_path": "...\\store_tarot_shiro.png", "target": "hon_x64"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
If `Target window not found` occurs during an interactive capture sequence, check `kongor_server_manage action: 'status'` and restart the cluster/client via `kongor_client_navigate` (`authMode: 'cluster'`, `ensureCluster: true`, `autoBoot:...
