---
id: MCP-002
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "K2 UI Interface Global Nil (Attempt to Index Global 'interface')"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "McpToolContractTests.MCP002_And_MCP051_And_MCP119_LuaInterfaceRemediation_PidWindowTargeting_AndSyncEvalPayload"
superseded_by: ""
supersedes: ["MCP-005", "MCP-016", "MCP-049", "MCP-071", "MCP-078", "MCP-079", "MCP-085", "MCP-093"]
occurrences: 17
date: 2026-09-24
last_seen: 2026-11-15
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "`ClientCommandRemediationService` auto-rewrites `interface:UICmd(...)` $\to$ `Cmd(...)`."
---

# MCP-002: K2 UI Interface Global Nil (Attempt to Index Global 'interface')

## Summary
Tracked MCP issue on `kongor` / `kongor_game_lua` (category: `runtime-error`, status: `codified`, occurrences: 17). Reconnect UI dismissed and reconnect state cleared. -> Threw Lua runtime error: `attempt to index global 'interface' (a nil value)`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Reconnect UI dismissed and reconnect state cleared. |
| **Actual (Symptom)** | Threw Lua runtime error: `attempt to index global 'interface' (a nil value)`. |
| **Root Cause** | In K2 Lua scripting, `interface` is only exposed locally inside specific XML interface script blocks, never as a global symbol in root script evaluations. The root UI manager is `UIManager`. |
| **Fix / Remediation** | Code patch in `src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs`: Added auto-remediation rule that rewrites `interface:UICmd\s*\(([^)]+)\)` into `Cmd($1)`, returning the auto-remediated command and marking `selfHealed: true`. - Added unit test in `tests/unit/tools/Kongor.Mcp.Tests/ClientCommandRemediationTests.cs`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  Script local m = UIManager.GetInterface('main')
  if m then
      local rd = m:GetWidget('reconnect_dialog')
      if rd then rd:SetVisible(false) end
  end
  interface:UICmd('ClearReconnectInfo()')
  

Logs:

  [ERROR] [string "local m = UIManager.GetInterface('main')..."]:6: attempt to index global 'interface' (a nil value)
  client_command_errors.ndjson: Id 55a24f5e, ErrorCategory: ExecutionError
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`ClientCommandRemediationService` auto-rewrites `interface:UICmd(...)` $\to$ `Cmd(...)`.
