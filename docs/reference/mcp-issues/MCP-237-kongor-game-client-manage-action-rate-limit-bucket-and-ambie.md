---
id: MCP-237
server: kongor
tool: kongor_game_client_manage
category: rate-limit
title: "KONGOR-GAME-CLIENT-MANAGE-ACTION-RATE-LIMIT-BUCKET-AND-AMBIENT-STOP"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-18
last_seen: 2026-11-04
tags: [kongor, kongor_game_client_manage, rate-limit, ambient-client, stop]
prevention_rule: "Fix McpToolRegistry.cs:696 and McpRateLimiter.cs to route kongor_game_client_manage action:status/pipes/logs to ReadOnly (60/min) and action:command/lua to GameCommand, and fix GameClientManageHandler.cs:254-270 so action:stop terminates ambient hon_x64.exe instances when no explicit pid is passed."
---

# MCP-237: KONGOR-GAME-CLIENT-MANAGE-ACTION-RATE-LIMIT-BUCKET-AND-AMBIENT-STOP

## Summary
During the `2026-10-18` self-healing journey, two code defects in `kongor` / `kongor_game_client_manage` blocked client control:
1. **Action-Blind `ServerManagement` Rate Limit (Step `34571`):** Because `McpToolRegistry.cs:696` registers `kongor_game_client_manage` under `McpToolCategory.ServerManagement` (`5 calls / 60s` in `McpRateLimiter.cs:21`), read-only `action: "status"` / `"pipes"` / `"logs"` calls and IPC `action: "command"` / `"lua"` calls consume the 5-call/min `ServerManagement` token bucket and fail with `Rate limit exceeded for 'ServerManagement' category. Retry after 60s.`
2. **Refusal to Stop `"ambient"` Client Processes (Step `34187`):** Calling `kongor_game_client_manage({"action": "stop"})` when `hon_x64.exe` has `agentOwner: "ambient"` returns `terminatedProcessesCount: 0, message: "No running game client found for agent '...'"` without stopping the client unless `pid` or `all: true` is explicitly passed.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | 1. `action: "status"` / `"pipes"` / `"logs"` should use the `ReadOnly` rate-limit bucket (`60/min`), and `action: "command"` / `"lua"` should use `GameCommand` (`30–60/min`), reserving `ServerManagement` (`5/min`) strictly for heavy process lifecycle actions (`launch`, `stop`, `restart`).<br>2. `action: "stop"` (and `action: "restart"`) should automatically fall back to stopping the running `"ambient"` / primary `hon_x64.exe` process when the caller agent does not own a dedicated multi-slot client. |
| **Actual (Symptom)** | 1. Step `34571`: `Rate limit exceeded for 'ServerManagement' category. Retry after 60s.` on `action: "command"`.<br>2. Step `34187`: `{"status":"success","action":"stop","running":false,"terminatedProcessesCount":0,"message":"No running game client found for agent '91dcbae0-9afe-41dd-a48b-f16731d66aae'."}` while `hon_x64.exe` (PID `19256`, `agentOwner: "ambient"`) remained running. |
| **Root Cause** | 1. **Static Tool-Level Rate-Limit Category (`src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry.cs:696` & `McpRateLimiter.cs:16-28`):** Rate limiting is evaluated per tool name (`McpToolCategory.ServerManagement = 5/min`) before inspecting `arguments.action`, and `McpToolCategory.GameCommand` is also set too low (`10/min`) for rapid UI/Lua debugging.<br>2. **Strict `GetClientByAgentId(agentOwner)` Filter in `stop` (`src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs:254-270`):** When `agentOwner` is populated from `McpAgentContext.CurrentAgentId`, line `256` calls `_processRegistry.GetClientByAgentId(agentOwner)`. If the running `hon_x64.exe` was adopted as `"ambient"` (or launched before agent context was bound), `GetClientByAgentId` returns `null` and lines `261-269` return early with `terminatedProcessesCount = 0` instead of falling back to `_processRegistry.AutoAdoptRunningClient(agentOwner)` or `StopClient(null, null)`. |
| **Fix / Remediation** | 1. In [`McpToolRegistry.cs`](../../../src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry.cs#L690-L706) / [`McpRateLimiter.cs:16-28`](../../../src/tools/kongor-cli/Mcp/Protocol/McpRateLimiter.cs#L16-L28), resolve the rate-limit category dynamically from `arguments["action"]` for `kongor_game_client_manage` and `kongor_server_manage`: map `status`, `list`, `clients`, `pipes`, `pipe_status`, `logs`, `errors`, `history` -> `McpToolCategory.ReadOnly` (`60/min`); map `command`, `lua`, `set_window_mode`, `login` -> `McpToolCategory.GameCommand` (raise budget to `60/min`); keep `launch`, `stop`, `restart` -> `McpToolCategory.ServerManagement` (raise budget from `5/min` to `15/min`).<br>2. In [`GameClientManageHandler.cs:254-270`](../../../src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs#L254-L270) (and `restart` at lines `280-288`), replace `_processRegistry.GetClientByAgentId(agentOwner)` with `_processRegistry.GetClientByAgentId(agentOwner) ?? _processRegistry.AutoAdoptRunningClient(agentOwner) ?? _processRegistry.GetPrimaryClient()`, and if still `null`, call `StopClient(null, null)` when only a single ambient `hon_x64.exe` is running. |

## Invocation Parameters & Raw Logs
```text
Step 34187: kongor_game_client_manage({"action":"stop"})
-> {
  "status": "success",
  "action": "stop",
  "running": false,
  "terminatedProcessesCount": 0,
  "message": "No running game client found for agent '91dcbae0-9afe-41dd-a48b-f16731d66aae'."
}

Step 34571: kongor_game_client_manage({"action":"command","command":"Script Cvar.CreateCvar('_playerstats_match_owner', 'string', 'Maliken'); ..."})
-> Error: Rate limit exceeded for 'ServerManagement' category. Retry after 60s.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Fix `McpToolRegistry.cs:696` and `McpRateLimiter.cs` to route `kongor_game_client_manage` `action:status`/`pipes`/`logs` to `ReadOnly` (`60/min`) and `action:command`/`lua` to `GameCommand`, and fix `GameClientManageHandler.cs:254-270` so `action:stop` terminates ambient `hon_x64.exe` instances when no explicit `pid` is passed.
