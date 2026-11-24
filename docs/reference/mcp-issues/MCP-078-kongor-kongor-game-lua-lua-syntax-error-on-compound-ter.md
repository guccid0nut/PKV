---
id: MCP-078
server: kongor
tool: kongor_game_lua
category: runtime-error
title: "`kongor` kongor_game_lua Lua Syntax Error on Compound Ternary `w:GetNumChildren and w:GetNumChildren()`"
status: superseded
superseded_by: "MCP-002"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [kongor, kongor_game_lua, runtime-error]
prevention_rule: "`ClientCommandRemediationService` auto-rewrites `:\s*method\s+and` to `.$1 and` before execution."
---

# MCP-078: `kongor` kongor_game_lua Lua Syntax Error on Compound Ternary `w:GetNumChildren and w:GetNumChildren()`

## Summary
Repeat occurrence of [`MCP-002`] on `kongor` / `kongor_game_lua` (`runtime-error`). Superseded by `MCP-002`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Execution of recursive widget traversal script. |
| **Actual (Symptom)** | Returned `[ERROR] Lua syntax error: [string "..."]:10: function arguments expected near 'and'`. |
| **Root Cause** | In Lua grammar, method call syntax `w:GetNumChildren` cannot be evaluated as an expression without invocation arguments `w:GetNumChildren(...)`. Testing for method presence must use `w.GetNumChildren` (dot syntax) rather than `w:GetNumChildren` (colon syntax). |
| **Fix / Remediation** | Corrected syntax to use dot accessor `w.GetNumChildren` before calling. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "code",
    "code": "local count = w:GetNumChildren and w:GetNumChildren() or 0",
    "target": "client"
  }
  

Logs:

  [ERROR] Lua syntax error: [string "local main = UIManager.GetInterface('main')..."]:10: function arguments expected near 'and'
  remediationCategory: LuaSyntaxError
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
`ClientCommandRemediationService` auto-rewrites `:\s*method\s+and` to `.$1 and` before execution.
