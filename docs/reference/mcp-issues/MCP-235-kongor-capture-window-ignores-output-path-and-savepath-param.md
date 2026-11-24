---
id: MCP-235
server: kongor
tool: kongor_capture_window
category: parameter-mismatch
title: "KONGOR-CAPTURE-WINDOW-IGNORES-OUTPUT-PATH-AND-SAVEPATH-PARAMETERS"
status: patched
verified_by: "Kongor.Mcp.Tests.McpToolContractTests.Mcp235_ResolveCustomOutputPath_AcceptsAllAliasesAndHandlesNullShortCircuit, Mcp235_ResolveArtifactPath_EnforcesAllowlistAndExtension, Mcp235_CaptureWindowHandler_MapsWindowNotFoundToClientOfflinePreflight"
superseded_by: ""
supersedes: []
occurrences: 5
date: 2026-10-18
last_seen: 2026-10-27
tags: [kongor, kongor_capture_window, parameter-mismatch, screenshot, output-path]
prevention_rule: "Pass any recognized output path alias (file_path, output_path, save_path, destination, custom_output_path) to kongor_capture_window; paths are normalized against repo root and written even if save_artifact is false."
---

# MCP-235: KONGOR-CAPTURE-WINDOW-IGNORES-OUTPUT-PATH-AND-SAVEPATH-PARAMETERS

## Summary
During the `2026-10-18` self-healing journey (Steps `35278`, `35283`, `35307`), callers invoked `kongor` / `kongor_capture_window` with `output_path`, `outputPath`, and `savePath` to save screenshots to deterministic artifact paths, but `CaptureWindowHandler` silently ignored several parameter names, short-circuited on null properties, dropped output when `save_artifact: false`, and normalized relative paths to `%TEMP%\kongor_mcp_runtime\pid_*\` instead of the repository root. Patched across `CaptureWindowHandler.cs`, `WindowCapturer.cs`, `UiRenderViewHandler.cs`, and `McpToolRegistry.LuaUi.cs`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_capture_window`, `kongor_transient_capture`, `kongor_ui_render_view` |
| **Expected** | Save the captured PNG screenshot to the path specified by `output_path`, `outputPath`, `save_path`, `savePath`, `file_path`, `filePath`, `artifact_path`, `artifactPath`, `destination`, `path`, or `custom_output_path`. Honor explicit paths even if `save_artifact: false`. Normalize relative paths against repository root. |
| **Actual (Symptom)** | Silently ignored non-`custom_output_path` parameter names, dropped files when `save_artifact: false`, and wrote to `%TEMP%\kongor_mcp_runtime\pid_*\artifacts\captures\capture_hon_x64_*.png`. Offline client errors were also misclassified under MCP-235. |
| **Root Cause** | (1) Parameter aliases were missing from schema and handler; (2) `||` short-circuiting in handler blocked fallbacks when an earlier key had a null/empty string; (3) `WindowCapturer.cs` guarded saving behind `if (saveArtifact)` even when custom output paths were provided; (4) Relative paths resolved against `AppDomain.CurrentDomain.BaseDirectory` instead of `McpServer.FindProjectRoot()`; (5) `WindowNotFound` lacked `errorCode = "clientOffline"` so offline errors defaulted to `tool_fault`. |
| **Fix / Remediation** | (1) Implemented `CaptureWindowHandler.ResolveCustomOutputPath` checking all 12 aliases safely; (2) Updated schemas in `McpToolRegistry.LuaUi.cs`; (3) Implemented `WindowCapturer.ResolveArtifactPath` with directory allowlisting, `.png` extension enforcement, and decoupling from `saveArtifact`; (4) Normalized all paths against `FindProjectRoot()`; (5) Mapped `WindowNotFound` / `InvalidWindowHandle` to `errorCode = "clientOffline"` and `preflightFailed = true`. |

## Invocation Parameters & Raw Logs
```text
Step 35278: kongor_capture_window({"output_path":"<scratch_dir>\\dev_alt9_fixed.png","target":"hon_x64"})
-> {"status":"success","filePath":"%TEMP%\\kongor_mcp_runtime\\pid_73216\\artifacts\\captures\\capture_hon_x64_20261018_211329_752.png"}

Step 35283: kongor_capture_window({"outputPath":"<scratch_dir>\\dev_alt9_after.png","target":"hon_x64"})
-> {"status":"success","filePath":"%TEMP%\\kongor_mcp_runtime\\pid_73216\\artifacts\\captures\\capture_hon_x64_20261018_211343_110.png"}

Step 35307: kongor_capture_window({"savePath":"<scratch_dir>\\sb_alt9_fixed.png","target":"hon_x64"})
-> {"status":"success","filePath":"%TEMP%\\kongor_mcp_runtime\\pid_73216\\artifacts\\captures\\capture_hon_x64_20261018_211512_408.png"}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_capture_window
```

## Prevention Invariant
Fix `CaptureWindowHandler.cs:123-128` to accept `output_path`, `outputPath`, `savePath`, `save_path`, and `path` aliases alongside `custom_output_path` so screenshots are written directly to the caller-requested file path.
