---
id: MCP-114
server: redis
tool: scan_keys
category: auth-config
title: "`redis` / `scan_keys` Connection Refused on Default Port 6379 (Garnet Mapped to 28298)"
status: superseded
superseded_by: "MCP-089"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [redis, scan_keys, auth-config]
prevention_rule: "Use TAO native cache tool `kongor_cache_coherence_verify` or direct C# / RESP socket to port 28298, or pass configured Garnet port."
---

# MCP-114: `redis` / `scan_keys` Connection Refused on Default Port 6379 (Garnet Mapped to 28298)

## Summary
Repeat occurrence of [`MCP-089`] on `redis` / `scan_keys` (`auth-config`). Superseded by `MCP-089`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `scan_keys` |
| **Expected** | Scan keys in Garnet/Redis cache. |
| **Actual (Symptom)** | Failed with `Error scanning keys with pattern '*SESSION*': Error 111 connecting to 127.0.0.1:6379. Connection refused.`. |
| **Root Cause** | The generic Redis MCP server container `mcp/redis` connects to default Redis port `6379` unless `REDIS_PORT=28298` is provided in its container environment. In ProjectKongor, Garnet cache listens on port `28298`. |
| **Fix / Remediation** | Recorded in issues log; query Garnet via port 28298. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "pattern": "*SESSION*"
  }
  

Logs:

  Error scanning keys with pattern '*SESSION*': Error 111 connecting to 127.0.0.1:6379. Connection refused.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool scan_keys
```

## Prevention Invariant
Use TAO native cache tool `kongor_cache_coherence_verify` or direct C# / RESP socket to port 28298, or pass configured Garnet port.
