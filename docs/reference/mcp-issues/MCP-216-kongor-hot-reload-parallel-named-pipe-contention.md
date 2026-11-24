---
id: MCP-216
server: kongor
tool: kongor_hot_reload
category: window-resolution
title: "KONGOR-HOT-RELOAD-PARALLEL-NAMED-PIPE-CONTENTION"
status: superseded
superseded_by: "MCP-210"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [kongor, kongor_hot_reload, window-resolution]
prevention_rule: "Never invoke `kongor_hot_reload` in parallel with another `kongor_*` IPC tool call; serialize IPC calls sequentially or dispatch `ReloadInterfaces` directly via `kongor_game_client_manage` (`action: 'command'`)."
---

# MCP-216: KONGOR-HOT-RELOAD-PARALLEL-NAMED-PIPE-CONTENTION

## Summary
Repeat occurrence of [`MCP-210`] on `kongor` / `kongor_hot_reload` (`window-resolution`). Superseded by `MCP-210`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_hot_reload` |
| **Expected** | Trigger `ReloadInterfaces` and `dofile('/ui/scripts/nexus_bridge.lua')`. |
| **Actual (Symptom)** | `ReloadInterfaces` succeeded on the first pipe request, while the second step returned `Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable` due to concurrent pipe contention with the parallel `kongor_game_client_manage` call. |
| **Root Cause** | `\\.\pipe\kongor-native-client` (`version.dll`) is a single-instance named pipe. Dispatching `kongor_hot_reload` and `kongor_game_client_manage` in the same parallel tool batch causes one caller to hit pipe-busy while `ReloadInterfaces` is executing on the main window thread. |
| **Fix / Remediation** | Never invoke `kongor_hot_reload` in parallel with another `kongor_*` IPC tool call; serialize IPC calls sequentially or dispatch `ReloadInterfaces` directly via `kongor_game_client_manage` (`action: "command"`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "trigger"}` (invoked concurrently in parallel with `kongor_game_client_manage`)
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_hot_reload
```

## Prevention Invariant
Never invoke `kongor_hot_reload` in parallel with another `kongor_*` IPC tool call; serialize IPC calls sequentially or dispatch `ReloadInterfaces` directly via `kongor_game_client_manage` (`action: 'command'`).
