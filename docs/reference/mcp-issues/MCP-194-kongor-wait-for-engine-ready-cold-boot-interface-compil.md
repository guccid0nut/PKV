---
id: MCP-194
server: kongor
tool: kongor_wait_for
category: window-resolution
title: "KONGOR-WAIT-FOR-ENGINE-READY-COLD-BOOT-INTERFACE-COMPILE-WINDOW"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: ["MCP-193"]
occurrences: 26
date: 2026-10-13
last_seen: 2026-11-05
tags: [kongor, kongor_wait_for, window-resolution]
prevention_rule: "Pass `timeout_ms: 25000` when calling `kongor_wait_for` (`condition: 'engine_ready'`) immediately after a cold `kongor_game_client_manage action: 'launch'`."
---

# MCP-194: KONGOR-WAIT-FOR-ENGINE-READY-COLD-BOOT-INTERFACE-COMPILE-WINDOW

## Summary
Tracked MCP issue on `kongor` / `kongor_wait_for` (category: `window-resolution`, status: `investigating`, occurrences: 26). Return `matched: true` once `hon_x64.exe` finishes cold-booting. -> Timed out at `15111ms` because `hon_x64.exe` was still compiling `main_v2.interface` and loading 3D login pedestal models (~17s cold-boot). Linked with `MCP-193` / `MCP-136` cold-boot eval timeout chain.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | Return `matched: true` once `hon_x64.exe` finishes cold-booting. |
| **Actual (Symptom)** | Timed out at `15111ms` because `hon_x64.exe` was still compiling `main_v2.interface` and loading 3D login pedestal models (~17s cold-boot). |
| **Root Cause** | Cold-booting `hon_x64.exe` with 3D login scene (`login_v2.interface` + `main_v2.interface`) takes ~16-18 seconds before the main window loop pumps IPC frames, slightly exceeding a 15,000ms timeout. |
| **Fix / Remediation** | Implemented layered readiness (`ipc_ready` -> `engine_ready` requiring 2 advancing `CHost::Frame` ticks -> `client_ready`), native `msSinceHostFrameAdvance` tracking in `version.dll` (`0x0005`), and the default budget-aware pre-send engine gate in `ClientEvalLuaHandler` (`commit aca377a7b`). Verified across `5/5` cold-boot canary restarts (`120/120` named-pipe evals, `0` mailbox fallbacks, `13.4s-15.6s` per cold boot in `logs/shj/mcp194_cold_boot_5x_summary.json`) and live `ReloadInterfaces` (`waitedForEngineMs=1243.23ms`, `logs/shj/d2_live_reload_presend_gate_probe.json`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"condition": "engine_ready", "timeout_ms": 15000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
Pass `timeout_ms: 25000` when calling `kongor_wait_for` (`condition: 'engine_ready'`) immediately after a cold `kongor_game_client_manage action: 'launch'`.
