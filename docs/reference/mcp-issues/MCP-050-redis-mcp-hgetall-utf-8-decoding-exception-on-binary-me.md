---
id: MCP-050
server: redis
tool: hgetall
category: binary-decode
title: "Redis MCP hgetall UTF-8 Decoding Exception on Binary MemoryPack Hash Values"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-046", "MCP-053", "MCP-057", "MCP-067"]
occurrences: 5
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, binary-decode]
prevention_rule: "`redis_mcp_wrapper.py` interceptor & native TAO cache inspector decode binary into structured hex/base64."
---

# MCP-050: Redis MCP hgetall UTF-8 Decoding Exception on Binary MemoryPack Hash Values

## Summary
Tracked MCP issue on `redis` / `hgetall` (category: `binary-decode`, status: `patched`, occurrences: 5). Retrieve hash entries stored under key `MATCH-SERVERS`. -> Failed with `'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash entries stored under key `MATCH-SERVERS`. |
| **Actual (Symptom)** | Failed with `'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte`. |
| **Root Cause** | The upstream Redis MCP server implementation of `hgetall` attempts to decode all Redis hash values as UTF-8 strings. When keys like `MATCH-SERVERS` contain binary serialized payloads (e.g. MemoryPack bytes starting with non-ASCII headers), the Python UTF-8 codec raises a `UnicodeDecodeError`. |
| **Fix / Remediation** | Created `src/tools/redis/redis_mcp_wrapper.py` & Docker image `mcp/redis:latest` which intercepts `ToolManager.call_tool` and safely encodes binary non-UTF8 hash payloads into structured hex/base64 representations. - Implemented native high-performance cache inspection directly into TAO's `kongor_cache_coherence_verify` (`action: "hgetall"` / `"ins... |

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
`redis_mcp_wrapper.py` interceptor & native TAO cache inspector decode binary into structured hex/base64.
