---
id: MCP-245
server: kongor
tool: kongor_wait_for
category: false-positive-ready
title: "kongor_wait_for condition lua treats EvalLua ERROR string as truthy satisfied"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/WaitForHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-10-25
last_seen: 2026-11-04
tags: [kongor, kongor_wait_for, false-positive-ready]
prevention_rule: "In WaitForHandler.cs condition 'lua', reject responses starting with '[ERROR]' or 'false'/'nil'/'0' instead of treating non-empty error strings as satisfied: true."
---

# MCP-245: kongor_wait_for condition lua treats EvalLua ERROR string as truthy satisfied

## Summary
`WaitForHandler` (`kongor_wait_for`) with `condition: "lua"` previously dispatched `ExecuteCommandAsync("Script return tostring(...)")` (`0x0003`) instead of synchronous `ExecuteLuaAsync` (`0x000E`), and evaluated any non-empty string other than `"nil"`, `"false"`, or `"0"` as `satisfied: true`—causing `[ERROR] ...`, `[LUA_ERR] ...`, and `[OK] Command queued` ACKs to return false-positive success. Resolved by routing through `ExecuteLuaAsync`, enforcing per-probe deadline cancellation, rejecting error/queued-ACK prefixes, and honoring optional `expected` matching.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | `condition: "lua"` evaluates the target Lua expression via `0x000E` `EvalLuaSync`, treats `[ERROR]`/`[LUA_ERR]`/`[OK] Command queued` as unsatisfied errors, and only returns `satisfied: true` on genuine truthy values or `expected` matches. |
| **Actual (Symptom)** | Returned `status: "success", satisfied: true` immediately on poll 1 when `ExecuteCommandAsync` returned `"[OK] Command queued to K2 main thread ring"` or when `EvalLuaSync` returned `"[ERROR] attempt to index a nil value"`. |
| **Root Cause** | `WaitForHandler.cs` (`case "lua":`) called `_gameIpcBridge.ExecuteCommandAsync` (`0x0003`) instead of `_gameIpcBridge.ExecuteLuaAsync` (`0x000E`) and lacked error-prefix / queued-ACK rejection before checking `!= "nil" && != "false" && != "0"`. |
| **Fix / Remediation** | Updated `WaitForHandler.cs` (`case "lua":`) to dispatch via `targetBridge.ExecuteLuaAsync` with a deadline-clamped `CancellationTokenSource`, reject `[ERROR]`, `[LUA_ERR]`, `[SECURITY ERROR]`, `[OK] Command queued`, and `{"success":false}` responses as `satisfied: false` (`isEvalError = true`), and honor `expected` string equality when provided. |

## Invocation Parameters & Raw Logs
```text
kongor_wait_for({"condition":"lua","target":"NonExistentGlobal.foo","timeout_ms":500})
-> Previously returned {"status":"success","satisfied":true,"observedState":{"output":"[ERROR] attempt to index global 'NonExistentGlobal' (a nil value)"}}
-> Now polls until timeout and returns {"status":"timeout","satisfied":false,"observedState":{"output":"[ERROR] ...","isEvalError":true}}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
In WaitForHandler.cs condition 'lua', reject responses starting with '[ERROR]' or 'false'/'nil'/'0' instead of treating non-empty error strings as satisfied: true.
