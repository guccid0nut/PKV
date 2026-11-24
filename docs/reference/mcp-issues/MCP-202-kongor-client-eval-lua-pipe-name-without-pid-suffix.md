---
id: MCP-202
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "KONGOR-CLIENT-EVAL-LUA-PIPE-NAME-WITHOUT-PID-SUFFIX"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Dispatch live Lua/console commands to `\\.\pipe\kongor-native-client-{pid}` via `src/tools/tao_mcp.py` when `kongor-native-client-{pid}` is active."
---

# MCP-202: KONGOR-CLIENT-EVAL-LUA-PIPE-NAME-WITHOUT-PID-SUFFIX

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Reload `main` interface in running `hon_x64.exe` (`PID 32460`). |
| **Actual (Symptom)** | Returned `Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable.` |
| **Root Cause** | `NativeGuard` (`version.dll`) creates the PID-scoped named pipe `\\.\pipe\kongor-native-client-32460`, whereas `kongor_client_eval_lua` attempted to connect to the un-suffixed `\\.\pipe\kongor-native-client`. |
| **Fix / Remediation** | Dispatch live Lua/console commands to `\\.\pipe\kongor-native-client-{pid}` via `src/tools/tao_mcp.py` when `kongor-native-client-{pid}` is active. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "reload_ui", "timeout_ms": 5000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Dispatch live Lua/console commands to `\\.\pipe\kongor-native-client-{pid}` via `src/tools/tao_mcp.py` when `kongor-native-client-{pid}` is active.
