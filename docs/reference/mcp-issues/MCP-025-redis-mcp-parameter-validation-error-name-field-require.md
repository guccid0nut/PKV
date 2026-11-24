---
id: MCP-025
server: redis
tool: hgetall
category: parameter-mismatch
title: "Redis MCP Parameter Validation Error: 'name' Field Required instead of 'key' in `hgetall`"
status: superseded
superseded_by: "MCP-052"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, parameter-mismatch]
prevention_rule: "Always pass `name` instead of `key` to `redis / hgetall`."
---

# MCP-025: Redis MCP Parameter Validation Error: 'name' Field Required instead of 'key' in `hgetall`

## Summary
Repeat occurrence of [`MCP-052`] on `redis` / `hgetall` (`parameter-mismatch`). Superseded by `MCP-052`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash dictionary for key `MATCH-SERVERS`. |
| **Actual (Symptom)** | Threw validation error: `1 validation error for hgetallArguments: name: Field required [type=missing, input_value={'key': 'MATCH-SERVERS'}, input_type=dict]`. |
| **Root Cause** | The Redis MCP `hgetall` tool schema names the target hash key parameter `name`, not `key`. |
| **Fix / Remediation** | Invoked `redis / hgetall` with `name: "MATCH-SERVERS"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "key": "MATCH-SERVERS"
  }
  

Logs:

  Error executing tool hgetall: 1 validation error for hgetallArguments
  name
    Field required [type=missing, input_value={'key': 'MATCH-SERVERS'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
Always pass `name` instead of `key` to `redis / hgetall`.
