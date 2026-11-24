---
id: MCP-233
server: kongor
tool: kongor_get_client_logs
category: runtime-error
title: "KONGOR-GET-CLIENT-LOGS-MISSING-USERDATA-UTF16LE-CONSOLE-LOG-AND-IPC-SCRIPT-PREFIX"
status: patched
verified_by: "McpToolContractTests.MCP231_And_MCP233_GetClientLogsHandler_ParsesUtf16LeConsoleLog_AndHonorsLinesAndFilterAliases"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-18
last_seen: 2026-10-18
tags: [kongor, kongor_get_client_logs, runtime-error, logs, utf-16le]
prevention_rule: "Fix GetClientLogsHandler.cs:64-194 to read UTF-16LE console.log from games/client/userdata/Heroes of Newerth x64/override/console.log (calling FlushLogs first), prefix IPC Lua ring-buffer queries with Script, and accept lines/tailLines aliases."
---

# MCP-233: KONGOR-GET-CLIENT-LOGS-MISSING-USERDATA-UTF16LE-CONSOLE-LOG-AND-IPC-SCRIPT-PREFIX

## Summary
During the `2026-10-18` self-healing journey (Steps `34377`, `34608`, `34825`, `35041`), **4 out of 4** calls to `kongor` / `kongor_get_client_logs` took **7,053–7,068 ms** and silently returned `"totalLogsReturned": 0, "logs": []`, forcing agents to spawn PowerShell commands to flush and read `games/client/userdata/Heroes of Newerth x64/override/console.log`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_get_client_logs` |
| **Expected** | Flush the K2 console log buffer (`FlushLogs`) and return the latest `N` log lines from `console.log` / `client_lua_debug.log` (supporting `filter` / `severity`) in `< 100ms`. |
| **Actual (Symptom)** | Stalls for `7,060ms` and returns `{"status":"success","source":"disk","totalLogsReturned":0,"logs":[],"elapsedMs":7060.15}` even when `games/client/userdata/Heroes of Newerth x64/override/console.log` has thousands of lines. |
| **Root Cause** | 1. **Unprefixed Multi-Line Lua Sent to `ExecuteCommandAsync` (`src/tools/kongor-cli/Mcp/Handlers/GetClientLogsHandler.cs:90-98`):** When `source` is `"auto"` (default), `ReadIpcRingBufferAsync` sends a multi-line Lua snippet containing `{}` table literals directly to `_ipcBridge.ExecuteCommandAsync(luaCode, 3000)` **without** a `"Script "` prefix, and expects the `0x0003` pipe ACK to return serialized log lines—stalling for 7 seconds before falling back to `ReadDiskLogs`.<br>2. **Missing K2 `userdata/Heroes of Newerth x64` Paths & UTF-16LE Encoding (`GetClientLogsHandler.cs:176-189`):** `ReadDiskLogs` only checks `games/client/override/logs/client_lua_debug.log`, `games/client/game/logs/client_lua_debug.log`, and `Documents/Heroes of Newerth/game/logs/*.log`. It **completely omits** the actual K2 64-bit log paths:<br>- `games/client/userdata/Heroes of Newerth x64/override/console.log`<br>- `games/client/userdata/Heroes of Newerth x64/game/console.log`<br>- `games/client/userdata/Heroes of Newerth x64/override/logs/client_lua_debug.log`<br>And it does not send `FlushLogs` via IPC before reading disk logs, nor does it handle UTF-16LE (`FF FE`) encoding explicitly when tailing.<br>3. **Ignored Parameter Aliases (`GetClientLogsHandler.cs:64-68`):** Only checks `limit` and `count`, ignoring `lines` and `tailLines` passed by callers. |
| **Fix / Remediation** | 1. In [`GetClientLogsHandler.cs:64-68`](../../../src/tools/kongor-cli/Mcp/Handlers/GetClientLogsHandler.cs#L64-L68), accept `lines`, `tailLines`, `tail_lines`, `maxLines`, `limit`, and `count`.<br>2. Before reading disk logs, if `_ipcBridge is { IsConnected: true }`, send `_ipcBridge.ExecuteCommandAsync("FlushLogs", 500)` so K2 flushes its buffered `console.log` to disk.<br>3. In [`GetClientLogsHandler.cs:176-194`](../../../src/tools/kongor-cli/Mcp/Handlers/GetClientLogsHandler.cs#L176-L194) (and `GameClientManageHandler.GetClientLogsAsync`), add `games/client/userdata/Heroes of Newerth x64/override/console.log`, `games/client/userdata/Heroes of Newerth x64/game/console.log`, and `games/client/userdata/Heroes of Newerth x64/override/logs/*.log` as the primary disk log sources, reading with `detectEncodingFromByteOrderMarks: true` (`UTF-16LE` support). |

## Invocation Parameters & Raw Logs
```text
Step 34377: kongor_get_client_logs({"lines":50})
-> {"status":"success","source":"disk","severityFilter":"all","textFilter":null,"totalLogsReturned":0,"logs":[],"elapsedMs":7060.15}

Step 34608: kongor_get_client_logs({"lines":40})
-> {"status":"success","source":"disk","severityFilter":"all","textFilter":null,"totalLogsReturned":0,"logs":[],"elapsedMs":7053.42}

Step 34825: kongor_get_client_logs({"filter":"SCAN_","lines":50})
-> {"status":"success","source":"disk","severityFilter":"all","textFilter":"SCAN_","totalLogsReturned":0,"logs":[],"elapsedMs":7068.11}

Step 35041: kongor_get_client_logs({"tailLines":60})
-> {"status":"success","source":"disk","severityFilter":"all","textFilter":null,"totalLogsReturned":0,"logs":[],"elapsedMs":7058.9}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_get_client_logs
```

## Prevention Invariant
Fix `GetClientLogsHandler.cs:64-194` to read UTF-16LE `console.log` from `games/client/userdata/Heroes of Newerth x64/override/console.log` (calling `FlushLogs` first), prefix IPC Lua ring-buffer queries with `Script`, and accept `lines`/`tailLines` aliases.
