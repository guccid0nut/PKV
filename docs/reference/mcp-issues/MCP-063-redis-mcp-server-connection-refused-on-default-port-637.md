---
id: MCP-063
server: redis
tool: scan_all_keys
category: auth-config
title: "Redis MCP Server Connection Refused on Default Port 6379 (Garnet Runs on 28298)"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, scan_all_keys, auth-config]
prevention_rule: "`redis_mcp_wrapper.py` defaults `REDIS_PORT` to `28298` (Garnet) and TAO provides native `kongor_cache_coherence_verify`."
---

# MCP-063: Redis MCP Server Connection Refused on Default Port 6379 (Garnet Runs on 28298)

## Summary
Tracked MCP issue on `redis` / `scan_all_keys` (category: `auth-config`, status: `patched`, occurrences: 1). Return all active Redis keys from local cache store. -> Failed with error: `Error scanning all keys with pattern '*': Error 111 connecting to 127.0.0.1:6379. Connection refused.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `scan_all_keys` |
| **Expected** | Return all active Redis keys from local cache store. |
| **Actual (Symptom)** | Failed with error: `Error scanning all keys with pattern '*': Error 111 connecting to 127.0.0.1:6379. Connection refused.` |
| **Root Cause** | The project runs Microsoft Garnet / Redis cache on port `28298` (as specified in GEMINI.md). The Redis MCP server default environment configuration pointed to standard Redis port `6379`. |
| **Fix / Remediation** | Code patch in `src/tools/redis/redis_mcp_wrapper.py`: Configured default `os.environ.setdefault("REDIS_PORT", "28298")` and `os.environ.setdefault("REDIS_HOST", "127.0.0.1")`. - Added native TAO cache inspection tool `kongor_cache_coherence_verify` (`action: "hgetall"`, `"hget"`, `"inspect"`, `"keys"`) with binary-safe MemoryPack hex/base64 deseria... |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "match": "*"
  }
  

Logs:

  Error scanning all keys with pattern '*': Error 111 connecting to 127.0.0.1:6379. Connection refused.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool scan_all_keys
```

## Prevention Invariant
`redis_mcp_wrapper.py` defaults `REDIS_PORT` to `28298` (Garnet) and TAO provides native `kongor_cache_coherence_verify`.
