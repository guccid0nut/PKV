---
id: MCP-298
server: kongor
tool: kongor_capture_window
category: capture
title: "Requested capture method silently falls back to window capture"
status: resolved
codified_in: "tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs"
verified_by: "be14b0551 + tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs + tests/python/test_tao_mcp_stdio_conformance.py + logs/last_gate_run.json"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_capture_window, capture]
prevention_rule: "An explicitly requested capture method either succeeds or fails; fallbacks happen only for auto and are reported with the reason."
---

# MCP-298: Requested capture method silently falls back to window capture

## Summary
`kongor_capture_window` with an explicit `capture_method: vulkan_offscreen` previously returned a `print_window` capture (`zeroWindowExposure: false`) with `status: success`, and transient capture could expose the game window without explicit opt-in. Resolved in Horizon 2.5 + Horizon 2.6 (`be14b0551`): explicit `capture_method` requests never silently fall back (`auto` reports `fallbackReason`), and `transient` / focus-exposing capture returns `isError: true` (`errorCode: "window_exposure_blocked"`) unless `allowWindowExposure: true` (`allow_window_exposure: true`), `steal_focus: true`, or `KONGOR_MCP_ALLOW_WINDOW_EXPOSURE=1` is set.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window` |
| **Expected** | An explicitly requested method succeeds or fails. Only `auto` may fall back, never to a method that shows or focuses the window unless the caller allows it, and the response states the fallback reason. |
| **Actual (Symptom)** | Two of roughly 45 captures fell back to `print_window` (one requested as `vulkan_offscreen`, about 0.7-0.8 s each). `target: client` returned `captureMethod: print_window`. `kongor_client_capture_preview` reported `captureMethodUsed: window_dc`. No response carried a fallback reason. |
| **Root Cause** | `WindowCapturer.cs` cascades offscreen, `print_window`, `window_dc`, `desktop_dc` and then a transient capture (around lines 585-605); the transient path calls `SetWindowPos` with topmost at (0, 0) and `SetForegroundWindow` (around lines 1099-1102). Why the offscreen frame was unavailable for the two fallbacks was not determined (compare `MCP-048`, `MCP-061`). |
| **Fix / Remediation** | Fixed in `be14b0551` (Horizon 2.6). Explicit capture methods fail cleanly instead of cascading, `auto` emits `fallbackReason`, and window-exposing capture requires `allowWindowExposure: true` (`allow_window_exposure`), `steal_focus: true`, or `KONGOR_MCP_ALLOW_WINDOW_EXPOSURE=1`. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
row 432 kongor_capture_window {capture_method: vulkan_offscreen} -> captureMethod print_window, zeroWindowExposure false, 809 ms
row 78  kongor_capture_window {}                                 -> captureMethod print_window, 671 ms
row 233 kongor_capture_window {target: client}                   -> captureMethod print_window
row 306 kongor_client_capture_preview                            -> captureMethodUsed window_dc
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
An explicitly requested capture method either succeeds or fails; fallbacks happen only for auto and are reported with the reason.
