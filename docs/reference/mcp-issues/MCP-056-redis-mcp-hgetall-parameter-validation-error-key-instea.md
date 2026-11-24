---
id: MCP-056
server: redis
tool: hgetall
category: parameter-mismatch
title: "Redis MCP hgetall Parameter Validation Error ('key' instead of 'name')"
status: superseded
superseded_by: "MCP-052"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, parameter-mismatch]
prevention_rule: "`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` alias `key` $\leftrightarrow$ `name`."
---

# MCP-056: Redis MCP hgetall Parameter Validation Error ('key' instead of 'name')

## Summary
Repeat occurrence of [`MCP-052`] on `redis` / `hgetall` (`parameter-mismatch`). Superseded by `MCP-052`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve all fields and values from hash `MATCH-SERVERS`. |
| **Actual (Symptom)** | Failed with Pydantic validation error: `Field required [type=missing, input_value={'key': 'MATCH-SERVERS'}, input_type=dict]` for field `name`. |
| **Root Cause** | The Redis MCP server schema for `hgetall` expects parameter `name`, not `key`. |
| **Fix / Remediation** | Hardened parameter normalization in `redis_mcp_wrapper.py` and TAO `kongor_cache_coherence_verify`. |

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
`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` alias `key` $\leftrightarrow$ `name`.
