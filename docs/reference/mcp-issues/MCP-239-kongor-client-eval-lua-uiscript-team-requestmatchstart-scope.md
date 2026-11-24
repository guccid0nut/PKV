---
id: MCP-239
server: kongor
tool: kongor_client_eval_lua
category: lua-eval
title: "KONGOR-CLIENT-EVAL-LUA-UISCRIPT-TEAM-REQUESTMATCHSTART-SCOPE"
status: investigating
codified_in: "src/tools/kongor-cli/Mcp/Handlers/ClientEvalLuaHandler.cs"
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 11
date: 2026-10-19
last_seen: 2026-11-17
tags: [kongor, kongor_client_eval_lua, lua-eval]
prevention_rule: "Always dispatch K2 UIScript lobby commands (Team, RequestMatchStart, Ready) through UIManager.GetInterface('main'):UICmd(...) rather than calling them as global Lua functions."
---

# MCP-239: KONGOR-CLIENT-EVAL-LUA-UISCRIPT-TEAM-REQUESTMATCHSTART-SCOPE

## Summary
Calling `Team(1, 0)` or `RequestMatchStart()` directly as global Lua functions in `kongor_client_eval_lua` during `gamePhase 1` (Practice Lobby) fails with `attempt to call global 'Team' (a nil value)` because they are K2 UIScript functions rather than Lua globals.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Join Legion slot 0 (`Team(1, 0)`) and start the match (`RequestMatchStart()`) from `gamePhase 1`. |
| **Actual (Symptom)** | `[LUA_ERR] [string "Team(1, 0)..."]:1: attempt to call global 'Team' (a nil value)`. |
| **Root Cause** | `Team(1, 0)` and `RequestMatchStart()` are K2 UIScript commands registered in the UIScript command table (`system_bar.package:1402`) rather than the global Lua state `_G`. |
| **Fix / Remediation** | Dispatch via `UIManager.GetInterface('main'):UICmd("Team(1, 0); RequestMatchStart();")` in `gamePhase 1`, followed by `UIManager.GetInterface('main'):UICmd("SpawnHero('Hero_Devourer'); Ready();")` in `gamePhase 3`. |

## Invocation Parameters & Raw Logs
```text
{"lua": "Team(1, 0)\nRequestMatchStart()\nreturn \"CALLED_TEAM_AND_REQUEST_MATCH_START\""}
-> [LUA_ERR] [string "Team(1, 0)..."]:1: attempt to call global 'Team' (a nil value)
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Always dispatch K2 UIScript lobby commands (`Team`, `RequestMatchStart`, `Ready`) through `UIManager.GetInterface('main'):UICmd(...)` rather than calling them as global Lua functions.
