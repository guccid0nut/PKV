---
id: MCP-260
server: kongor
tool: kongor_game_client_manage
category: ipc-collision
title: "Multi-slot launch (slot >= 1) omits KONGOR_NATIVE_PIPE isolation and command fallback hits Slot 0"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-11-04
last_seen: 2026-11-09
tags: [kongor, kongor_game_client_manage, ipc-collision]
prevention_rule: "Until GameClientManageHandler sets per-slot KONGOR_NATIVE_PIPE, pass launch-time credentials/connectAddress to slot >= 1 instead of post-launch pid IPC commands."
---

# MCP-260: Multi-slot launch (slot >= 1) omits KONGOR_NATIVE_PIPE isolation and command fallback hits Slot 0

## Summary
Launching a secondary headless game client (`slot >= 1`) via `kongor` MCP tool `kongor_game_client_manage` (`action: 'launch'`) omits `KONGOR_NATIVE_PIPE` environment isolation in `GameClientManageHandler.Launch.cs`, causing `version.dll` (`NativeGuard`) in Slot 1 to open a competing `PIPE_UNLIMITED_INSTANCES` server handle on default `\\.\pipe\kongor-native-client` while `action: 'command'` with `pid` falls back to Slot 0's channel when `-pipe hon-client-1` is neutralized.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | `action: 'launch'` with `slot: 1` binds an isolated `NativeGuard` pipe (e.g. `\\.\pipe\kongor-native-client-1`), and `action: 'command'` with `pid: <slot1Pid>` routes deterministically to Slot 1 without touching Slot 0. |
| **Actual (Symptom)** | `GameIpcBridge("hon-client-1")` times out after 1000 ms because `NativeGuard` neutralizes legacy `-pipe`, `GameClientManageHandler.Command.cs:198` logs `[WARN] Dedicated IPC for client PID ... (hon-client-1) failed ... Falling back to default channel`, and both Slot 0 and Slot 1 share `\\.\pipe\kongor-native-client` (`PIPE_UNLIMITED_INSTANCES`). |
| **Root Cause** | `GameClientManageHandler.Launch.cs:135` sets `pipeName = "hon-client-1"` on `-pipe`, which `native/proxies/native_guard/dllmain.cpp:6973` neutralizes, while `GameClientManageHandler.Launch.cs:212` never sets `startInfo.EnvironmentVariables["KONGOR_NATIVE_PIPE"]` (`dllmain.cpp:6274`), causing Slot 1's `NativeCommandPipeWorker` (`dllmain.cpp:6302`) to bind a second `PIPE_UNLIMITED_INSTANCES` server instance on `\\.\pipe\kongor-native-client`. |
| **Fix / Remediation** | Tag Slot 0 in Lua (`_G.__QA_SLOT = 0`) before launching Slot 1 and route multi-client `0x574D` NativeGuard pipe frames by probing `_G.__QA_SLOT` across `npfs.sys` FIFO instances (or pass launch-time credentials), and follow up in `GameClientManageHandler.Launch.cs` to set `KONGOR_NATIVE_PIPE = $"kongor-native-client-{slotIdx}"` for `slotIdx > 0`. |

## Invocation Parameters & Raw Logs
```text
{"action": "launch", "slot": 1, "window_mode": "headless", "resolution": "1280x720", "fullscreen": false}
[WARN] Dedicated IPC for client PID 87980 (hon-client-1) failed: Timeout waiting for pipe connection. Falling back to default channel.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Until GameClientManageHandler sets per-slot KONGOR_NATIVE_PIPE, tag Slot 0 before launching Slot 1 and use per-slot NativeGuard instance routing (or launch-time credentials/connectAddress) instead of post-launch pid IPC commands.
