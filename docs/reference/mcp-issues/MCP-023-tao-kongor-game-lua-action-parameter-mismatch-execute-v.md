---
id: MCP-023
server: kongor
tool: kongor_game_lua
category: parameter-mismatch
title: "TAO `kongor_game_lua` Action Parameter Mismatch ('execute' vs 'code')"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [kongor, kongor_game_lua, parameter-mismatch]
prevention_rule: "`GameLuaHandler` accepts `action: 'execute'`, `'run'`, `'eval'`, `'script'` as aliases for `'code'`."
---

# MCP-023: TAO `kongor_game_lua` Action Parameter Mismatch ('execute' vs 'code')

## Summary
Tracked MCP issue on `kongor` / `kongor_game_lua` (category: `parameter-mismatch`, status: `patched`, occurrences: 1). Execute Lua script in the game client. -> Threw error: `Unknown action 'execute'. Supported actions: 'hide_login', 'open_store', 'practice_match', 'apply_snapshot', 'apply_fixture', 'set_timescale', 'step_frames', 'pause', 'resume', 'collect_telemetry', 'inspect_ui', 'navigate', 'protect_queues', 'tao_dispatch', 'code', 'errors', 'history'`...

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Execute Lua script in the game client. |
| **Actual (Symptom)** | Threw error: `Unknown action 'execute'. Supported actions: 'hide_login', 'open_store', 'practice_match', 'apply_snapshot', 'apply_fixture', 'set_timescale', 'step_frames', 'pause', 'resume', 'collect_telemetry', 'inspect_ui', 'navigate', 'protect_queues', 'tao_dispatch', 'code', 'errors', 'history'`... |
| **Root Cause** | The `kongor_game_lua` tool expects `action: "code"` when executing arbitrary Lua snippets, whereas `kongor_game_command` uses `action: "execute"`. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Handlers/GameLuaHandler.cs`: Added `action: "execute"`, `"run"`, `"eval"`, `"script"` as recognized action aliases falling through directly to the `"code"` execution handler. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "execute",
    "script": "Player_Hosted_V2:OpenCreate()"
  }
  

Logs:

  Encountered error in tool execution: Unknown action 'execute'. Supported actions: 'hide_login', 'open_store', 'practice_match', 'apply_snapshot', 'apply_fixture', 'set_timescale', 'step_frames', 'pause', 'resume', 'collect_telemetry', 'inspect_ui', 'navigate', 'protect_queues', 'tao_dispatch', 'code', 'errors', 'history'.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`GameLuaHandler` accepts `action: 'execute'`, `'run'`, `'eval'`, `'script'` as aliases for `'code'`.
