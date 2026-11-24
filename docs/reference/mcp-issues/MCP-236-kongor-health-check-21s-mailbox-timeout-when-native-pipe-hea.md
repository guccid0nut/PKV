---
id: MCP-236
server: kongor
tool: kongor_health_check
category: ipc-timeout
title: "KONGOR-HEALTH-CHECK-21S-MAILBOX-TIMEOUT-WHEN-NATIVE-PIPE-HEALTHY"
status: patched
verified_by: "McpToolContractTests.P1_3_TerseByDefault_And_MCP236_HealthCheckHandler_UsesFastPipeCheck"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-18
last_seen: 2026-10-18
tags: [kongor, kongor_health_check, ipc-timeout, mailbox, tao]
prevention_rule: "Fix HealthCheckHandler.cs:201-258 (ProbeMailboxAsync) to probe \\\\.\\pipe\\kongor-native-client directly (1.3ms latency) instead of stalling 21.3s on legacy TaoClient file-mailbox/EvalLuaSync timeouts and falsely reporting status: degraded."
---

# MCP-236: KONGOR-HEALTH-CHECK-21S-MAILBOX-TIMEOUT-WHEN-NATIVE-PIPE-HEALTHY

## Summary
During the `2026-10-18` self-healing journey (Steps `34165`, `34177`, `34191`, `34869`), **4 out of 4** calls to `kongor` / `kongor_health_check` took **21,150–21,365 ms** and returned `status: "degraded", healthy: false` (`mailbox.connected: false, version: "offline"`) even when `hon_x64.exe` was running, logged in, and responding in `1.3ms` over `\\.\pipe\kongor-native-client`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_health_check` |
| **Expected** | Verify `hon_x64.exe` process liveness, IPC pipe connectivity (`\\.\pipe\kongor-native-client`), and recent log/minidump cleanliness in `< 100ms`, returning `status: "healthy", healthy: true`. |
| **Actual (Symptom)** | Stalls for `21,316ms` and returns `status: "degraded", healthy: false` with `checks.mailbox: {"connected":false,"pingLatencyMs":0,"version":"offline"}`. |
| **Root Cause** | In [`src/tools/kongor-cli/Mcp/Handlers/HealthCheckHandler.cs:201-258`](../../../src/tools/kongor-cli/Mcp/Handlers/HealthCheckHandler.cs#L201-L258) (`ProbeMailboxAsync`) and `ProbeTaoCapabilitiesAsync` (lines `260-296`), `HealthCheckHandler` calls `_taoClient.DispatchAsync("ping", ...)` (`1500ms`), `_taoClient.DispatchAsync("health_check", ...)` (`1500ms`), and `_taoCapabilitiesHandler.HandleAsync(...)`. Each of those `TaoClient` calls falls back to `ClientEvalLuaHandler` (`MCP-231`), which spins for `10,050ms` per call (`2 x 10.05s + 1.2s = 21.3s` total!) and fails with `connected: false`. `HealthCheckHandler` never checks `_gameIpcBridge.GetPipeStatusAsync()` or `IpcOpCode.Ping (0x0001)` on `\\.\pipe\kongor-native-client`! |
| **Fix / Remediation** | 1. Inject `IGameIpcBridge` into [`HealthCheckHandler.cs`](../../../src/tools/kongor-cli/Mcp/Handlers/HealthCheckHandler.cs#L27-L50).<br>2. In `ProbeMailboxAsync` (lines `201-258`), first check `_gameIpcBridge.GetPipeStatusAsync(ct)` (which pings `\\.\pipe\kongor-native-client` in `~1.3ms`). If the native client pipe is connected, mark `mailbox.Connected = true` and `PingLatencyMs = pipeStatus.LatencyMs` immediately without stalling 21 seconds on legacy `TaoClient` file-mailbox fallbacks.<br>3. Fix `MCP-231` (`0x000E` `EvalLuaSync`) so `TaoClient` dispatches also complete in `< 10ms`. |

## Invocation Parameters & Raw Logs
```text
Step 34165: kongor_health_check({})
-> {"status":"degraded","healthy":false,"elapsedMs":21192.42,"checks":{"client":{"running":true,"processId":19256},"mailbox":{"connected":false,"pingLatencyMs":0,"version":"offline"}}}

Step 34869: kongor_health_check({})
-> {"status":"degraded","healthy":false,"elapsedMs":21316.01,"checks":{"client":{"running":true,"processId":73216,"workingSetMb":759.22,"slotIndex":0,"agentOwner":"ambient"},"mailbox":{"connected":false,"pingLatencyMs":0,"version":"offline"},"tao":{"probed":true,"isStale":null,"handlersCount":0,"build":"offline"},"errors":{"clean":true,"errorCount":0,"warningCount":0,"minidumpCount":0}}}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_health_check
```

## Prevention Invariant
Fix `HealthCheckHandler.cs:201-258` (`ProbeMailboxAsync`) to probe `\\.\pipe\kongor-native-client` directly (`1.3ms` latency) instead of stalling 21.3s on legacy `TaoClient` file-mailbox/`EvalLuaSync` timeouts and falsely reporting `status: degraded`.
