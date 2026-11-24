---
id: MCP-142
server: kongor
tool: kongor_game_client_manage
category: transport-eof
title: "(formerly MCP-081) `kongor` MCP Server Transport EOF on Client Closing"
status: resolved
verified_by: "McpToolContractTests.MCP138_And_MCP141_And_MCP142_RateLimitIsolation_NativePipeNoFallback_AndGracefulClientStatus"
superseded_by: ""
supersedes: ["MCP-181", "MCP-148"]
occurrences: 3
date: 2026-09-28
last_seen: 2026-10-07
tags: [kongor, kongor_game_client_manage, transport-eof]
prevention_rule: "When the `kongor` MCP server connection terminates with EOF, directly use native Windows PowerShell cmdlets (`Get-Process hon_x64`, `Get-CimInstance Win32_Process`) and direct Nexus IPC (`req_current.lua` / `res_current.txt`) for engine con..."
---

# MCP-142: (formerly MCP-081) `kongor` MCP Server Transport EOF on Client Closing

## Summary
Tracked MCP issue on `kongor` / `kongor_game_client_manage` (category: `transport-eof`, status: `resolved`, occurrences: 3). Return client lifecycle status and active window mode. -> Failed with error: `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Return client lifecycle status and active window mode. |
| **Actual (Symptom)** | Failed with error: `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The `kongor` MCP server child process terminated or disconnected its stdio transport stream while handling previous commands, causing subsequent JSON-RPC calls over the closed pipe to fail with EOF. |
| **Fix / Remediation** | Workflow fell back immediately to Windows PowerShell process management and Nexus IPC file polling. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "status"
  }
  

Logs:

  Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
When the `kongor` MCP server connection terminates with EOF, directly use native Windows PowerShell cmdlets (`Get-Process hon_x64`, `Get-CimInstance Win32_Process`) and direct Nexus IPC (`req_current.lua` / `res_current.txt`) for engine con...
