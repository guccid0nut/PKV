---
id: MCP-082
server: redis
tool: delete
category: auth-config
title: "External `redis` MCP Server Hardcoded to Port 6379 Instead of Cluster Port 28298"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [redis, delete, auth-config]
prevention_rule: "Prioritize TAO's native in-tree tool `kongor_cache_coherence_verify` or direct RESP socket commands to port `28298` for Redis/Garnet operations as specified in `AGENTS.md` and `GEMINI.md`."
---

# MCP-082: External `redis` MCP Server Hardcoded to Port 6379 Instead of Cluster Port 28298

## Summary
Tracked MCP issue on `redis` / `delete` (category: `auth-config`, status: `resolved`, occurrences: 1). Delete Redis key `MATCH-SERVERS` in local Garnet cache. -> Failed with `Error deleting key MATCH-SERVERS: Error 111 connecting to 127.0.0.1:6379. Connection refused.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `delete` |
| **Expected** | Delete Redis key `MATCH-SERVERS` in local Garnet cache. |
| **Actual (Symptom)** | Failed with `Error deleting key MATCH-SERVERS: Error 111 connecting to 127.0.0.1:6379. Connection refused.` |
| **Root Cause** | The external Docker-based `redis` MCP server connects to standard Redis port `6379`. In ProjectKongor, Garnet is hosted on port `28298` (`kongor-garnet-cache`). |
| **Fix / Remediation** | Executed RESP `DEL` directly over TCP to `127.0.0.1:28298` and verified with `kongor_cache_coherence_verify`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "key": "MATCH-SERVERS"
  }
  

Logs:

  Error deleting key MATCH-SERVERS: Error 111 connecting to 127.0.0.1:6379. Connection refused.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool delete
```

## Prevention Invariant
Prioritize TAO's native in-tree tool `kongor_cache_coherence_verify` or direct RESP socket commands to port `28298` for Redis/Garnet operations as specified in `AGENTS.md` and `GEMINI.md`.
