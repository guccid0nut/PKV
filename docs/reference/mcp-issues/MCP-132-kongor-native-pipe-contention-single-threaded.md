---
id: MCP-132
server: kongor
tool: NexusMailboxIpcBridge
category: runtime-error
title: "KONGOR-NATIVE-PIPE-CONTENTION-SINGLE-THREADED"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-12
last_seen: 2026-10-12
tags: [kongor, NexusMailboxIpcBridge, runtime-error]
prevention_rule: "Patched in Step 5 (`native/proxies/version_proxy/dllmain.cpp`): `NativeCommandPipeThread` now spawns detached `ClientPipeSessionThread` workers per connection so unlimited concurrent pipe clients can connect simultaneously."
---

# MCP-132: KONGOR-NATIVE-PIPE-CONTENTION-SINGLE-THREADED

## Summary
Tracked MCP issue on `kongor` / `NexusMailboxIpcBridge` (category: `runtime-error`, status: `patched`, occurrences: 1). Standalone NexusMailboxIpcBridge connects to `\\.\pipe\kongor-native-client` to signal K2 engine. -> `System.InvalidOperationException: Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `NexusMailboxIpcBridge` |
| **Expected** | Standalone NexusMailboxIpcBridge connects to `\\.\pipe\kongor-native-client` to signal K2 engine. |
| **Actual (Symptom)** | `System.InvalidOperationException: Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable.` |
| **Root Cause** | `dllmain.cpp` creates the native pipe with `PIPE_UNLIMITED_INSTANCES` but executes a single-threaded blocking `ConnectNamedPipe` -> `ReadFile` loop. It cannot accept a second connection while one client is active. - In `CompositeGameIpcBridge`, `NativeClientIpcBridge` already held the connection, causing `NexusMailboxIpcBridge`'s separate `GameIpcB... |
| **Fix / Remediation** | Patched in Step 5 (`native/proxies/version_proxy/dllmain.cpp`): `NativeCommandPipeThread` now spawns detached `ClientPipeSessionThread` workers per connection so unlimited concurrent pipe clients can connect simultaneously. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"pipeName": "kongor-native-client"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool NexusMailboxIpcBridge
```

## Prevention Invariant
Patched in Step 5 (`native/proxies/version_proxy/dllmain.cpp`): `NativeCommandPipeThread` now spawns detached `ClientPipeSessionThread` workers per connection so unlimited concurrent pipe clients can connect simultaneously.
