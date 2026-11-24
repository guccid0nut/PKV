---
id: MCP-273
server: kongor
tool: kongor_capture_window
category: transport-frame-size
title: "kongor_capture_window inbound JSON-RPC frame exceeded maximum line length on high-entropy 1080p PNG"
status: documented
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-15
last_seen: 2026-11-15
tags: [kongor, kongor_capture_window, transport-frame-size]
prevention_rule: "Use scripts/capture_verified_window.ps1 -OutputPath <path> when capturing high-entropy 1080p/1440p terrain scenes whose inline PNG Base64 response exceeds stdio JSON-RPC line limits."
---

# MCP-273: kongor_capture_window inbound JSON-RPC frame exceeded maximum line length on high-entropy 1080p PNG

## Summary
When invoking `kongor` / `kongor_capture_window` against an offscreen `hon_x64` client rendering high-frequency cobblestone/foliage terrain at 1440p/1080p, the tool writes the PNG artifact to disk successfully, but serializing the multi-megabyte inline Base64 PNG inside a single NDJSON stdio response triggers `inbound JSON-RPC frame exceeded the configured maximum line length` in the MCP host transport and closes the stdio pipe for subsequent calls.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | Save the captured PNG to `output_path` and return capture metadata without exceeding the MCP host's stdio JSON-RPC frame size limit. |
| **Actual (Symptom)** | Disk PNG is written, then tool execution fails with `calling "tools/call": inbound JSON-RPC frame exceeded the configured maximum line length`, disconnecting the `kongor` stdio transport. |
| **Root Cause** | High-entropy full-resolution (`2560x1440` / `1920x1080`) cobblestone/foliage terrain screenshots compress poorly as lossless PNG (>3 MB raw, >4 MB Base64), exceeding the MCP host's maximum single-line JSON-RPC stdio buffer when returned inline. |
| **Fix / Remediation** | Use `scripts/capture_verified_window.ps1 -OutputPath <path>` (combined with `scripts/send_cmd.ps1 -TargetPipe kongor-native-client` when stdio is disconnected) to write full-resolution captures directly to disk without inline Base64 JSON-RPC transport framing. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"detect_anomalies":false,"output_path":"artifacts/captures/enfos_postfx_0.png","target":"hon_x64"}

Error:
Encountered error in tool execution: calling "tools/call": inbound JSON-RPC frame exceeded the configured maximum line length
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Use `scripts/capture_verified_window.ps1 -OutputPath <path>` when capturing high-entropy 1080p/1440p terrain scenes whose inline PNG Base64 response exceeds stdio JSON-RPC line limits.
