---
id: MCP-026
server: redis
tool: hset
category: parameter-mismatch
title: "Redis MCP Parameter Validation Error: Stringified Dict Object Required for 'value' in `hset`"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hset, parameter-mismatch]
prevention_rule: "`redis_mcp_wrapper.py` auto-serializes non-string `value` and dictionary `mapping` to JSON strings."
---

# MCP-026: Redis MCP Parameter Validation Error: Stringified Dict Object Required for 'value' in `hset`

## Summary
Tracked MCP issue on `redis` / `hset` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Set field `guccihost:3001` in `MATCH-SERVERS` hash. -> Threw validation error: `3 validation errors for hsetArguments: value.str: Input should be a valid string, value.int: Input should be a valid integer, value.float: Input should be a valid number`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hset` |
| **Expected** | Set field `guccihost:3001` in `MATCH-SERVERS` hash. |
| **Actual (Symptom)** | Threw validation error: `3 validation errors for hsetArguments: value.str: Input should be a valid string, value.int: Input should be a valid integer, value.float: Input should be a valid number`. |
| **Root Cause** | The Redis MCP `hset` schema expects `value` to be a primitive string, integer, or float (`Union[str, int, float]`), not an object/dict. JSON payloads must be explicitly serialized to a string before invocation. |
| **Fix / Remediation** | Serialized payload as a single-layer JSON string and passed to `redis / hset`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "name": "MATCH-SERVERS",
    "key": "guccihost:3001",
    "value": {"HostAccountID": 58, "Name": "^pDONUT ^oARENA ^g #K1", ...}
  }
  

Logs:

  Error executing tool hset: 3 validation errors for hsetArguments
  value.str
    Input should be a valid string
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hset
```

## Prevention Invariant
`redis_mcp_wrapper.py` auto-serializes non-string `value` and dictionary `mapping` to JSON strings.
