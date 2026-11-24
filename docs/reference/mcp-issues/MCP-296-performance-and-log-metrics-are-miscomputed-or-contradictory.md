---
id: MCP-296
server: kongor
tool: kongor_perf_window
category: telemetry
title: "Performance and log metrics are miscomputed or contradictory"
status: resolved
verified_by: "1ebe5f40d + f3c7add47 + Step 0 dual-ring live probe + PerfWindowHandler FastBus priority & lossless drain + PerfWindowTests"
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-17
last_seen: 2026-11-21
tags: [kongor, kongor_perf_window, telemetry]
prevention_rule: "Each metric has one source of truth; derive High-Hz FPS and frame pacing from vid_vulkan Local\\KongorTelemetryBuffer QPC deltas with lossless draining and never read version.dll's 20 Hz Local\\KongorClientTelemetry_<pid> guard ring as render frames."
---

# MCP-296: Performance and log metrics are miscomputed or contradictory

## Summary
Performance, latency and log-severity figures returned by different `kongor` tools contradicted each other or were internally inconsistent. Initial partial fixes landed in `1ebe5f40d` and `f3c7add47` (removing the `ReservedFlags & FlagHitch` check from `PerfWindowHandler`), while a 2026-11-21 live dual-ring probe proved the remaining root cause in `PerfWindowHandler.cs`: it still opened `version.dll`'s 20 Hz guard ring (`Local\KongorClientTelemetry_<pid>`) ahead of `vid_vulkan_x64.dll`'s per-rendered-frame ring (`Local\KongorTelemetryBuffer`), wrapped past 4,096 frames on >20s High-Hz windows, and computed frame stats from `CpuFrameTimeMs` alone instead of consecutive QPC frame deltas. Fully fixed in `PerfWindowHandler.cs` with `PerfWindowTests` regression coverage and live FastBus vs. `vid_vulkan_spy.log` (`<= 3%` divergence) enforcement in `scripts/qa/kongor_shj.ps1`. Related: `MCP-254`, `MCP-256`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_perf_window` |
| **Expected** | One source of truth per metric; `kongor_perf_window` matches `vid_vulkan_spy.log` `[HIGH_HZ_TELEMETRY]` within `<= 3%` across 20s 200+ Hz windows; log lines are classified by their own severity marker. |
| **Actual (Symptom)** | `kongor_perf_window measure` (2000 ms) reported `41 frames`, `averageFps: 20.4`, `avgFrameTimeMs: 5.78`, `hitchCount30ms: 41` with `maxFrameTimeMs: 7.16`, contradicting `kongor_game_client_manage status` (198–854 FPS) and `vid_vulkan_spy.log` (~200 FPS). Earlier 2026-11-17 audit also observed log-severity false positives and stale mailbox metadata (`1ebe5f40d` / `f3c7add47`). |
| **Root Cause** | Three compounded defects verified live on `PID 47916` (2026-11-21): (1) `PerfWindowHandler.OpenTelemetryBuffer` probed `Local\KongorClientTelemetry_{pid}` (written by `version.dll`'s `Sleep(50)` ~20 Hz monitor thread in `shm_telemetry.cpp:173-219`, producing 41 ticks / 2.07 s = 19.8 Hz) ahead of `vid_vulkan_x64.dll`'s per-rendered-frame `Local\KongorTelemetryBuffer`; (2) `version.dll` stores `g_voicePttFlags` in `ReservedFlags` bit 0, colliding with `FrameTelemetryRecord.FlagHitch` (`41/41` false hitches before `f3c7add47`); (3) `PerfWindowHandler` only read the final 4,096-slot snapshot at `stop` (wrapping silently on 20s @ 200+ Hz) and averaged `CpuFrameTimeMs` (`engineMs`) instead of consecutive `TimestampQpc` end-to-end frame deltas. |
| **Fix / Remediation** | (1) `f3c7add47` removed the unsafe `FlagHitch` check on unknown rings and resolved log/metadata inconsistencies. (2) `PerfWindowHandler.cs` now probes `Local\KongorTelemetryBuffer` first (`telemetrySource = "vid_vulkan_render_fastbus"`, `isRenderFrameAccurate = true`), falling back to `Local\KongorClientTelemetry_{pid}` (`isRenderFrameAccurate = false`, sentinel hitch flags disabled) only when `vid_vulkan` is absent. (3) Added a `100 ms` `PeriodicTimer` lossless background ring drainer tracking `engineFramesElapsed`, `sampledFrames`, and `droppedRingFrames`. (4) Computes true end-to-end QPC frame deltas, `medianFps`, `p1LowFps`, `p01LowFps`, `p50/p95/p99/p999FrameTimeMs`, High-Hz hitch buckets (`>8/>10/>16.67/>33.33/>50 ms`), and `sentinelHitchFlags`. Verified live (`FastBus` vs `vid_vulkan_spy.log` `0.03%–0.08%` divergence across `3,930` frames with `0` drops). |

## Invocation Parameters & Raw Logs
```text
Original 2026-11-17 symptom:
rows 36, 400 kongor_perf_window {measure, 2000 ms} -> 41 frames, averageFps 20.4, hitchCount30ms 41, maxFrameTimeMs 7.16

2026-11-21 live dual-ring verification (PID 47916, 2560x1440 @ 200Hz offscreen_paced, Frida OFF):
- Local\KongorClientTelemetry_47916 (version.dll 20 Hz guard ring): 41 ticks / 2.07s = 19.8 Hz, sentinelFlags.hitch = 41/41 (reproduced MCP-296 100%)
- Local\KongorTelemetryBuffer (vid_vulkan_x64.dll render FastBus, 100ms drain, 20s Window #3): 3,930 frames, 0 drops, FastBus AvgFPS = 196.02 vs vid_vulkan_spy.log AvgFPS = 195.97 (0.03% divergence), Median = 201.48 FPS, 1% Low = 62.02 FPS, P99 = 12.622 ms, gpuStall = 0
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter FullyQualifiedName~PerfWindowTests --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/qa/kongor_shj.ps1 -Action perf-canary -AllowClientRestart
```

## Prevention Invariant
Each metric has one source of truth; derive High-Hz FPS and frame pacing from `vid_vulkan` `Local\KongorTelemetryBuffer` QPC deltas with lossless draining and cross-check against `vid_vulkan_spy.log` (`<= 3%` divergence).
