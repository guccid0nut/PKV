---
id: MCP-046
server: redis
tool: hgetall
category: binary-decode
title: "Redis MCP UTF-8 Decode Error on Binary / MessagePack Serialized Hashes"
status: superseded
superseded_by: "MCP-050"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, binary-decode]
prevention_rule: "Patched via `redis_mcp_wrapper.py` & native TAO `kongor_cache_coherence_verify` safe binary representation."
---

# MCP-046: Redis MCP UTF-8 Decode Error on Binary / MessagePack Serialized Hashes

## Summary
Repeat occurrence of [`MCP-050`] on `redis` / `hgetall` (`binary-decode`). Superseded by `MCP-050`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash keys and values from Redis key `MATCH-SERVERS`. |
| **Actual (Symptom)** | Threw Python UTF-8 decoding exception: `'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte`. |
| **Root Cause** | In Microsoft Garnet / Redis cache for ProjectKongor, server manager and match server states in `MATCH-SERVERS` are binary-serialized via MessagePack / MemoryPack. The Python Redis MCP tool attempts to decode all hash field values as UTF-8 strings by default without fallback to hex/raw bytes. |
| **Fix / Remediation** | Query via direct service APIs or structured tools when inspecting binary-encoded caches. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "name": "MATCH-SERVERS"
  }
  

Logs:

  Encountered error in tool execution: Error executing tool hgetall: 'utf-8' codec can't decode byte 0xf6 in position 5: invalid start byte
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
Patched via `redis_mcp_wrapper.py` & native TAO `kongor_cache_coherence_verify` safe binary representation.
