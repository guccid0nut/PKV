---
id: MCP-254
server: kongor
tool: kongor_perf_window
category: false-positive
title: "Counts intentional 20 FPS unfocused background sleep as 50ms frame hitches"
status: resolved
verified_by: "f3c7add47 + McpToolContractTests + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-01
last_seen: 2026-11-17
tags: [kongor, kongor_perf_window, false-positive]
prevention_rule: "Check budgetViolationsCount and frameBudgetPassed when windowMode is headless/unfocused (host_backgroundMaxFPS=20 sleeps 50ms/frame), or raise host_backgroundMaxFPS before measuring hitchCount50ms."
---

# MCP-254: Counts intentional 20 FPS unfocused background sleep as 50ms frame hitches

## Summary
Profiling a background/offscreen `hon_x64.exe` client via `kongor` / `kongor_perf_window` reported elevated `hitchCount50ms` values (~20 per second) even though main-thread Lua and render execution took `< 1.0ms`, because K2's unfocused frame limiter (`host_backgroundMaxFPS 20`) intentionally sleeps `50ms` per frame when the game window does not hold Win32 foreground focus. Tracked as `open`; agents must raise `host_backgroundMaxFPS` (e.g. `Set host_backgroundMaxFPS 144`) before sampling wall-clock frame intervals on background/offscreen clients or inspect `budgetViolationsCount` / CPU work time.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_perf_window` |
| **Expected** | Distinguish genuine main-thread CPU/Lua stalls (`> 50ms` active execution) from intentional engine frame-rate throttling (`Sleep`) when running unfocused at `-32000,-32000`. |
| **Actual (Symptom)** | Reports `avgFps: 20.0`, `hitchCount50ms: 58` over a 3-second window while `hon_x64.exe` is offscreen/unfocused, despite `budgetViolationsCount: 0` on Lua/UI callbacks. |
| **Root Cause** | K2's `CHost::Execute` loop throttles unfocused windows to `host_backgroundMaxFPS` (default `20` FPS = `50.0ms` wall-clock frame delta). `PerfWindowHandler` (`src/tools/kongor-cli/Mcp/Handlers/PerfWindowHandler.cs`) computes `hitchCount50ms` from total wall-clock inter-frame deltas without subtracting K2's intentional limiter sleep or checking `host_backgroundMaxFPS` when unfocused. |
| **Fix / Remediation** | 1. Query `host_backgroundMaxFPS` and window focus state in `PerfWindowHandler` so intentional background frame-cap sleep is annotated rather than counted as a frame hitch.<br>2. Before running `kongor_perf_window` on an offscreen/background client, execute `Set host_backgroundMaxFPS 144` via `kongor_game_command` and verify `budgetViolationsCount` / `frameBudgetPassed`. |

## Invocation Parameters & Raw Logs
```json
{
  "durationMs": 3000,
  "sampleIntervalMs": 50
}
```
```json
{
  "status": "warning",
  "durationMs": 3000,
  "avgFps": 20.0,
  "p99FrameTimeMs": 50.4,
  "hitchCount50ms": 58,
  "budgetViolationsCount": 0,
  "frameBudgetPassed": true
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_perf_window
```

## Prevention Invariant
Check `budgetViolationsCount` and `frameBudgetPassed` when `windowMode` is headless/unfocused (`host_backgroundMaxFPS=20` sleeps `50ms`/frame), or run `Set host_backgroundMaxFPS 144` before measuring `hitchCount50ms` via `kongor_perf_window`.
