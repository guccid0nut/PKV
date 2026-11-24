---
id: MCP-248
server: kongor
tool: kongor_game_server_command
category: false-positive
title: "Reports success via managed pipe hon-server-PORT while command never executes on COMPEL worker"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-26
last_seen: 2026-11-06
tags: [kongor, kongor_game_server_command, false-positive]
prevention_rule: "On COMPEL workers, managed pipe hon-server-PORT forwards to stdin which K2 -dedicated ignores; verify execution via Slave-N log or use native pipe hon-server-native-PORT (0x0003/0x0004 ACK). Do not trust ipcResponse OK alone."
---

# MCP-248: Reports success via managed pipe hon-server-PORT while command never executes on COMPEL worker

## Summary
Dispatching console commands to a COMPEL-managed dedicated server worker via `kongor` / `kongor_game_server_command` returned a false-positive `{"status": "success", "ipcResponse": "OK"}` over the managed wrapper pipe `\\.\pipe\hon-server-<PORT>`, while the K2 dedicated engine (`hon_x64.exe -dedicated`) never executed the command because `-dedicated` on Windows uses its Win32 window console rather than reading `stdin`. Tracked as `open`; commands must route through the in-process NativeGuard pipe `\\.\pipe\hon-server-native-<PORT>` (`0x0003`/`0x0004`) and be verified in `Slave-N_<date>.log`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_server_command` |
| **Expected** | Execute the K2 console command (`ServerStatus`, `Echo`, `Set`) on the target dedicated server worker's main thread and confirm execution. |
| **Actual (Symptom)** | Tool returns `{"status": "success", "pipe": "hon-server-11235", "ipcResponse": "OK"}` in `< 20ms`, but `Slave-1_<date>.log` shows zero command execution and CVars remain unchanged. |
| **Root Cause** | COMPEL's managed process wrapper hosts `\\.\pipe\hon-server-<PORT>`, writes incoming command strings to `Process.StandardInput`, and immediately replies `"OK"`. However, `hon_x64.exe -dedicated` does not poll `STD_INPUT_HANDLE` for console commands; only `version.dll` (`NativeGuard`) hosted inside the worker (`\\.\pipe\hon-server-native-<PORT>`) drains commands directly onto K2's `CConsole` main-thread ring (`0x0003` -> `0x0004` `"[OK] Command queued to K2 main thread ring"`). |
| **Fix / Remediation** | 1. Prioritize `\\.\pipe\hon-server-native-<PORT>` (`OpCode 0x0003` / `0x000E`) in `GameServerCommandHandler` (`src/tools/kongor-cli/Mcp/Handlers/GameServerCommandHandler.cs`) and treat `hon-server-<PORT>` stdin writes as unverified unless `version.dll` native pipe is absent.<br>2. Verify side effects in `games/hon server/userdata/Heroes of Newerth x64/KONGOR/configuration/logs/Slave-N_<date>.log`. |

## Invocation Parameters & Raw Logs
```json
{
  "port": 11235,
  "command": "Echo [PROBE] dedicated_server_ping"
}
```
```json
{
  "status": "success",
  "port": 11235,
  "pipeName": "hon-server-11235",
  "ipcResponse": "OK"
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_server_command
```

## Prevention Invariant
On COMPEL workers, managed pipe `hon-server-PORT` forwards to `stdin` which K2 `-dedicated` ignores; verify execution via `Slave-N` log or use native pipe `hon-server-native-PORT` (`0x0003`/`0x0004` ACK). Do not trust `ipcResponse: "OK"` alone.
