---
id: MCP-057
server: redis
tool: hgetall
category: binary-decode
title: "Redis MCP hgetall UTF-8 Decode Failure on Binary Serialized Hash Values"
status: superseded
superseded_by: "MCP-050"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, binary-decode]
prevention_rule: "Binary-safe deserialization in `redis_mcp_wrapper.py` and TAO `kongor_cache_coherence_verify`."
---

# MCP-057: Redis MCP hgetall UTF-8 Decode Failure on Binary Serialized Hash Values

## Summary
Repeat occurrence of [`MCP-050`] on `redis` / `hgetall` (`binary-decode`). Superseded by `MCP-050`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash map of match servers. |
| **Actual (Symptom)** | Failed with Python UnicodeDecodeError: `'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte`. |
| **Root Cause** | Upstream Redis MCP client instantiated `decode_responses=True`, causing unhandled exceptions on non-UTF8 bytes in `MATCH-SERVERS`. |
| **Fix / Remediation** | Rebuilt `mcp/redis:latest` with binary payload fallback and implemented native `HGetAllAsync` in TAO's `CacheCoherenceVerifyHandler.cs`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "name": "MATCH-SERVERS"
  }
  

Logs:

  Error executing tool hgetall: 'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
Binary-safe deserialization in `redis_mcp_wrapper.py` and TAO `kongor_cache_coherence_verify`.
