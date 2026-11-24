---
id: MCP-102
server: kongor
tool: kongor_server_status
category: parameter-mismatch
title: "TAO `kongor_server_status` Unknown Tool Name Alias"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_server_status, parameter-mismatch]
prevention_rule: "Use canonical tool names `kongor_compel_manage` or `kongor_game_server_manage` for server status."
---

# MCP-102: TAO `kongor_server_status` Unknown Tool Name Alias

## Summary
Tracked MCP issue on `kongor` / `kongor_server_status` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Retrieve dedicated server fleet and worker status. -> Failed with `Unknown tool 'kongor_server_status'. Available tools: kongor_compel_manage, kongor_game_server_manage, ...`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_status` |
| **Expected** | Retrieve dedicated server fleet and worker status. |
| **Actual (Symptom)** | Failed with `Unknown tool 'kongor_server_status'. Available tools: kongor_compel_manage, kongor_game_server_manage, ...`. |
| **Root Cause** | `kongor_server_status` is an informal tool alias; TAO MCP server exposes `kongor_compel_manage` and `kongor_game_server_manage` for fleet and worker supervision. |
| **Fix / Remediation** | Use canonical tool `kongor_compel_manage` / `kongor_game_server_manage`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {}
  

Logs:

  Unknown tool 'kongor_server_status'. Available tools: asset_bake_pipeline, ... kongor_compel_manage, kongor_game_server_manage ...
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_server_status
```

## Prevention Invariant
Use canonical tool names `kongor_compel_manage` or `kongor_game_server_manage` for server status.
