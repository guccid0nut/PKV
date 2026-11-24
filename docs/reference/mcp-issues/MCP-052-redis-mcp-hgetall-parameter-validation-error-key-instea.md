---
id: MCP-052
server: redis
tool: hgetall
category: parameter-mismatch
title: "Redis MCP hgetall Parameter Validation Error ('key' instead of 'name')"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-025", "MCP-056"]
occurrences: 3
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, parameter-mismatch]
prevention_rule: "`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` accept `name` or `key` interchangeably."
---

# MCP-052: Redis MCP hgetall Parameter Validation Error ('key' instead of 'name')

## Summary
Tracked MCP issue on `redis` / `hgetall` (category: `parameter-mismatch`, status: `patched`, occurrences: 3). Retrieve hash keys and fields from Redis hash `MATCH-SERVER-MANAGERS`. -> Failed with Pydantic validation error: `Field required [type=missing, input_value={'key': 'MATCH-SERVER-MANAGERS'}, input_type=dict]`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash keys and fields from Redis hash `MATCH-SERVER-MANAGERS`. |
| **Actual (Symptom)** | Failed with Pydantic validation error: `Field required [type=missing, input_value={'key': 'MATCH-SERVER-MANAGERS'}, input_type=dict]`. |
| **Root Cause** | The upstream Redis MCP server schema for `hgetall` specifies parameter name `name` instead of `key` (matching standard redis-py `hgetall(name)` signature). |
| **Fix / Remediation** | Added parameter alias interception in `src/tools/redis/redis_mcp_wrapper.py` (`key` $\leftrightarrow$ `name`, `field` $\leftrightarrow$ `key`) and in TAO `kongor_cache_coherence_verify`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "key": "MATCH-SERVER-MANAGERS"
  }
  

Logs:

  Error executing tool hgetall: 1 validation error for hgetallArguments
  name
    Field required [type=missing, input_value={'key': 'MATCH-SERVER-MANAGERS'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` accept `name` or `key` interchangeably.
