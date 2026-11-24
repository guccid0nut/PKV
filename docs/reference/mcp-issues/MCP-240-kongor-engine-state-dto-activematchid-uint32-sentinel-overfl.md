---
id: MCP-240
server: kongor
tool: kongor_wait_for
category: schema-mismatch
title: "KONGOR-ENGINE-STATE-DTO-ACTIVEMATCHID-UINT32-SENTINEL-OVERFLOW"
status: resolved
verified_by: "McpToolContractTests.D0_EngineStateDto_GoldenCapturedLivePayload_WithUint32SentinelMatchId_DrivesWaitForEngineReadyOverRealPipe + logs/shj/mcp240_live_engine_ready_probe.json (commit 9e13073e8)"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-24
last_seen: 2026-10-24
tags: [kongor, kongor_wait_for, schema-mismatch, engine-state, ipc]
prevention_rule: "EngineStateDto numeric fields must match native snprintf widths: dllmain.cpp case 0x0005 emits activeMatchId with %u (4294967295 = no-match sentinel); deserialize as long/uint, never int. Verify with a golden captured 0x0005 payload test, not DTO mocks."
---

# MCP-240: KONGOR-ENGINE-STATE-DTO-ACTIVEMATCHID-UINT32-SENTINEL-OVERFLOW

## Summary
Calling `kongor` / `kongor_wait_for` (`condition: "engine_ready"`) against a live `hon_x64.exe` client in a local/MidWars match (`gamePhase == 3`) failed on every poll with `System.Text.Json.JsonException: The JSON value could not be converted to System.Int32. Path: $.activeMatchId` (category: `schema-mismatch`, status: `resolved`), breaking all 14 C# consumers of `IGameIpcBridge.GetEngineStateAsync` (`0x0005` -> `0x0006`). Resolved by aligning `EngineStateDto` with native `snprintf` unsigned widths (`%u` / `%llu`), normalizing K2's `0xFFFFFFFF` (`4294967295`) `INVALID_INDEX` sentinel to `0L` (`ServerMatchId = null`), making `HostFrame` nullable (`int?`) to distinguish missing-field legacy DLLs from cold-boot `0`, and verifying against both golden captured `0x0005` wire frames and live `hon_x64.exe` (`PID 636`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` (and all 14 `GetEngineStateAsync` callers) |
| **Expected** | `kongor_wait_for { "condition": "engine_ready" }` deserializes `0x0006` `EngineStateResponse`, observes `hostFrame` advancing across two polls, and returns `satisfied: true` in `< 200 ms`. |
| **Actual (Symptom)** | Timed out after 47 polls; every poll threw `The JSON value could not be converted to System.Int32. Path: $.activeMatchId \| BytePositionInLine: 101`. |
| **Root Cause** | In local/practice/bot matches, K2 sets `pGameInfo + 0xC0` (`m_uiMatchID`) to `INVALID_INDEX` (`0xFFFFFFFF` = `4294967295`). `native/proxies/native_guard/dllmain.cpp` (`case 0x0005`) formats `"activeMatchId":%u` (unsigned 32-bit), whereas `EngineStateDto.ActiveMatchId` in `GameIpcBridge.cs` was typed as signed `int` (`max 2147483647`). Prior unit tests constructed `EngineStateDto` instances directly instead of parsing captured native `0x0005` JSON. |
| **Fix / Remediation** | Changed `EngineStateDto.ActiveMatchId` and `ServerTick` to `long` with `Uint32SentinelZeroLongJsonConverter` (`4294967295` -> `0L`, `ServerMatchId => ActiveMatchId > 0 ? ActiveMatchId : null`), added `SafeUint32ToIntJsonConverter` / `SafeUInt64ToLongJsonConverter` across all `%u` and `%llu` fields on `EngineStateDto`, changed `HostFrame` to `int?` with deadline-clamped legacy Lua fallback in `WaitForHandler.cs`, and added golden wire-payload + real `NamedPipeServerStream` regression tests. |

## Invocation Parameters & Raw Logs
```text
Request:
{"name":"kongor_wait_for","arguments":{"condition":"engine_ready","timeout_ms":5000,"poll_interval_ms":100}}

Pre-fix raw 0x0005 capture (PID 636):
{"currentFps":179.7,"memoryWorkingSetBytes":2597322752,"luaMemoryKb":58107,"activeMatchId":4294967295,"currentMap":"midwars","playerName":"<scrubbed>","connectedPlayers":1,"uptimeSeconds":4974,"gamePhase":3,"gamePhaseName":"Match / In-Game (3)","isSpectator":false,"activeEntities":110,"serverTick":1336291,"hostFrame":1336291,"mainThreadBusy":false,"activeOpElapsedMs":0}

Post-fix live verification (logs/shj/mcp240_live_engine_ready_probe.json, PID 636):
{"status":"success","satisfied":true,"condition":"engine_ready","pollCount":2,"elapsedMs":136,"observedState":{"stage":"engine_ready","firstHostFrame":1560846,"hostFrame":1560865,"hostFrameAdvancing":true,"legacyLuaFallbackUsed":false,"mainThreadBusy":false,"activeMatchId":0,"gamePhase":3}}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter "FullyQualifiedName~D0_"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action check
```

## Prevention Invariant
EngineStateDto numeric fields must match native snprintf widths: dllmain.cpp case 0x0005 emits activeMatchId with %u (4294967295 = no-match sentinel); deserialize as long/uint, never int. Verify with a golden captured 0x0005 payload test, not DTO mocks.
