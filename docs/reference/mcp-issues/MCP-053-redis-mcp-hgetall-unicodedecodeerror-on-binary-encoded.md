---
id: MCP-053
server: redis
tool: hgetall
category: binary-decode
title: "Redis MCP hgetall UnicodeDecodeError on Binary-Encoded MemoryPack Payload in MATCH-SERVER-MANAGERS"
status: superseded
superseded_by: "MCP-050"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, binary-decode]
prevention_rule: "`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` decode non-UTF8 into hex/base64 without crashing."
---

# MCP-053: Redis MCP hgetall UnicodeDecodeError on Binary-Encoded MemoryPack Payload in MATCH-SERVER-MANAGERS

## Summary
Repeat occurrence of [`MCP-050`] on `redis` / `hgetall` (`binary-decode`). Superseded by `MCP-050`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash keys and fields from Redis hash `MATCH-SERVER-MANAGERS`. |
| **Actual (Symptom)** | Failed with `'utf-8' codec can't decode byte 0xf1 in position 5: invalid continuation byte`. |
| **Root Cause** | The Redis MCP server implementation of `hgetall` decodes all hash values as UTF-8 text strings. `MATCH-SERVER-MANAGERS` holds binary MemoryPack serialized data (written by `DistributedCacheExtensions.SerializeMatchServerManager`), which causes Python's UTF-8 decoder to fail. |
| **Fix / Remediation** | Patched in `mcp/redis:latest` and TAO `kongor_cache_coherence_verify` to safely convert binary payloads into structured hex preview and base64. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "name": "MATCH-SERVER-MANAGERS"
  }
  

Logs:

  Error executing tool hgetall: 'utf-8' codec can't decode byte 0xf1 in position 5: invalid continuation byte
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
`redis_mcp_wrapper.py` & `CacheCoherenceVerifyHandler` decode non-UTF8 into hex/base64 without crashing.
