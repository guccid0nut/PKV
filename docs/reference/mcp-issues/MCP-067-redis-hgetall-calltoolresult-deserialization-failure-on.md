---
id: MCP-067
server: redis
tool: hgetall
category: binary-decode
title: "`redis` hgetall CallToolResult Deserialization Failure on Binary Payloads"
status: superseded
superseded_by: "MCP-050"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, hgetall, binary-decode]
prevention_rule: "Prioritize TAO native `kongor_cache_coherence_verify` for binary MemoryPack structs."
---

# MCP-067: `redis` hgetall CallToolResult Deserialization Failure on Binary Payloads

## Summary
Repeat occurrence of [`MCP-050`] on `redis` / `hgetall` (`binary-decode`). Superseded by `MCP-050`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `hgetall` |
| **Expected** | Retrieve hash entries of Redis key `MATCH-SERVERS`. |
| **Actual (Symptom)** | Failed with FastMCP Pydantic schema validation error (`CallToolResult` expected dictionary or `TextContent` instance, received individual characters of an error/data string). |
| **Root Cause** | The external Python MCP server for Redis improperly formatted its JSON-RPC response when returning raw error/struct payloads from redis-py, returning strings where `CallToolResult.content` items were expected. |
| **Fix / Remediation** | Routed Redis state verification through native TAO `kongor_cache_coherence_verify` and Garnet REST/RESP endpoints. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "name": "MATCH-SERVERS"
  }
  

Logs:

  Encountered error in tool execution: 555 validation errors for CallToolResult
  content.0.TextContent: Input should be a valid dictionary or instance of TextContent
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool hgetall
```

## Prevention Invariant
Prioritize TAO native `kongor_cache_coherence_verify` for binary MemoryPack structs.
