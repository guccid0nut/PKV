---
id: MCP-203
server: kongor
tool: kongor_client_eval_lua
category: runtime-error
title: "KONGOR-CLIENT-EVAL-LUA-PIPE-NAME-WITHOUT-PID-SUFFIX-2"
status: superseded
superseded_by: "MCP-119"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_client_eval_lua, runtime-error]
prevention_rule: "Always check `MCP-202` and send `ReloadInterfaces` over `\\.\pipe\kongor-native-client-{pid}` directly rather than calling `kongor_client_eval_lua`."
---

# MCP-203: KONGOR-CLIENT-EVAL-LUA-PIPE-NAME-WITHOUT-PID-SUFFIX-2

## Summary
Repeat occurrence of [`MCP-119`] on `kongor` / `kongor_client_eval_lua` (`runtime-error`). Superseded by `MCP-119`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Reload `main` interface in running `hon_x64.exe` (`PID 32460`). |
| **Actual (Symptom)** | Returned `Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable.` |
| **Root Cause** | `NativeGuard` (`version.dll`) creates the PID-scoped named pipe `\\.\pipe\kongor-native-client-32460`, whereas `kongor_client_eval_lua` connects to `\\.\pipe\kongor-native-client`. |
| **Fix / Remediation** | Always check `MCP-202` and send `ReloadInterfaces` over `\\.\pipe\kongor-native-client-{pid}` directly rather than calling `kongor_client_eval_lua`. |

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
Always check `MCP-202` and send `ReloadInterfaces` over `\\.\pipe\kongor-native-client-{pid}` directly rather than calling `kongor_client_eval_lua`.
