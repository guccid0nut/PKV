---
id: MCP-231
server: kongor
tool: kongor_client_eval_lua
category: ipc-timeout
title: "KONGOR-CLIENT-EVAL-LUA-PCALL-LOADSTRING-SPINWAIT-TIMEOUT-AND-SEMICOLON-CORRUPTION"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: []
occurrences: 76
date: 2026-10-18
last_seen: 2026-11-18
tags: [kongor, kongor_client_eval_lua, ipc-timeout, version-dll, lua]
prevention_rule: "Fix ClientEvalLuaHandler.cs:149-156 and native/proxies/native_guard/dllmain.cpp:2249 so 0x0003 does not spin-wait 3000ms x 3 retries (10.05s) on inactive KongorSharedMem when payload contains pcall(loadstring(, and stop replacing semicolons inside Lua string literals."
---

# MCP-231: KONGOR-CLIENT-EVAL-LUA-PCALL-LOADSTRING-SPINWAIT-TIMEOUT-AND-SEMICOLON-CORRUPTION

## Summary
During the `2026-10-17`–`2026-10-18` self-healing journeys, **12 out of 12** calls to `kongor` / `kongor_client_eval_lua` against a live, responsive `hon_x64.exe` client timed out after **10,041–10,061 ms** with `"IPC request 'ExecuteCommand' timed out after 3000ms."` Additionally, any `kongor_game_command` containing `"pcall(loadstring("` (Step 34220) suffered the exact same 10.05s timeout because `version.dll` (`dllmain.cpp:2249`) intercepts that substring inside `0x0003` (`ExecuteCommand`) and enters a 3,000ms spin-wait on `KongorSharedMem`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Evaluate the Lua snippet on the K2 client main thread and return the serialized Lua return value within `< 50ms`. |
| **Actual (Symptom)** | Fails 100% of the time after `10,050ms` with `{"status":"error","error":"Failed to execute Lua snippet in client via IPC: IPC request 'ExecuteCommand' timed out after 3000ms.","elapsedMs":10051.51}`. |
| **Root Cause** | 1. **Primary `0x000E` (`EvalLuaSync`) Returns `null` When `KongorSharedMem` Is Unmapped (`src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs:111` & `native/proxies/native_guard/dllmain.cpp:2180-2240`):** `ExecuteLuaAsync` sends `IpcOpCode.EvalLuaSync (0x000E)`. In `dllmain.cpp`, `0x000E` queues a Lua command that writes `__kongor_eval_res` to `Cvar` and waits for the shared memory pump (`KongorSharedMem`) to copy the CVar out. When `KongorSharedMem` is not actively pumped on the main thread, `0x000E` fails or returns `null`.<br>2. **Fallback `0x0003` Triggers a 3,000ms Native Spin-Wait (`ClientEvalLuaHandler.cs:149-156` & `dllmain.cpp:2249`):** When `ExecuteLuaAsync` returns `null`, `ClientEvalLuaHandler.cs:154` falls back to `_ipcBridge.ExecuteCommandAsync("Script \"local _ok, _res = pcall(loadstring(...))\"", timeoutMs)`. Inside `dllmain.cpp:2249`, the `0x0003` (`ExecuteCommand`) handler checks `if (strstr(cmd, "pcall(loadstring(") != nullptr)` and enters a **3,000ms synchronous `Sleep(1)` spin-wait** on the pipe server thread waiting for `KongorSharedMem`! Meanwhile, `GameIpcBridge.SendRequestAsync` has a `3,000ms` pipe read timeout and retries **3 times** (`3 x 3,000ms + backoff = 10,050ms`), stalling the pipe server thread for 10 seconds and throwing `TimeoutException`!<br>3. **Semicolon Corruption in String Literals (`ClientEvalLuaHandler.cs:153`):** `rawLua.Replace("\"", "\\\"").Replace(";", " ")` blindly replaces all `;` characters with spaces, corrupting semicolons inside Lua strings (e.g. `UICmd("Foo(); Bar();")`). |
| **Fix / Remediation** | 1. In [`native/proxies/native_guard/dllmain.cpp:2180-2290`](../../../native/proxies/native_guard/dllmain.cpp#L2180-L2290), remove the `strstr(cmd, "pcall(loadstring(")` 3,000ms spin-wait from `0x0003` (`ExecuteCommand`), and in `0x000E` (`EvalLuaSync`) have the main-thread ring buffer Drain hook (`DrainCommandRingOnMainThread`) write the evaluated result of `__kongor_eval_res` directly into a dedicated static response buffer + `SetEvent(g_hEvalDoneEvent)` (or deterministic temp file) so `0x000E` returns in `< 10ms` without depending on `KongorSharedMem`.<br>2. In [`src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs:149-156`](../../../src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs#L149-L156), if `0x000E` is unavailable on an older loaded `version.dll`, fallback to queuing `Script ... Echo('KONGOR_EVAL|<id>|' .. tostring(_res)); Cmd('FlushLogs')` via `0x0003` (without the literal `"pcall(loadstring("` substring that triggers the legacy `version.dll` spin-wait!) and read the line matching `KONGOR_EVAL|<id>|` from `games/client/userdata/Heroes of Newerth x64/override/console.log` (UTF-16LE).<br>3. Never call `.Replace(";", " ")` on raw Lua code; encode the Lua chunk as hex or base64 or pass via `loadstring` with escaped quotes only. |

## Invocation Parameters & Raw Logs
```text
Steps 25269, 27489, 28034, 29026, 34180, 34191, 34202, 35024, 35039, 35063, 35132, 35608:
kongor_client_eval_lua({"code":"return tostring(GetCvarString('login_name'))"})
-> {
  "status": "error",
  "error": "Failed to execute Lua snippet in client via IPC: IPC request 'ExecuteCommand' timed out after 3000ms.",
  "code": "return tostring(GetCvarString('login_name'))",
  "elapsedMs": 10051.51
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Fix `ClientEvalLuaHandler.cs:149-156` and `native/proxies/native_guard/dllmain.cpp:2249` so `0x0003` does not spin-wait 3000ms x 3 retries (10.05s) on inactive `KongorSharedMem` when payload contains `pcall(loadstring(`, and stop replacing semicolons inside Lua string literals.
