---
id: MCP-089
server: redis
tool: scan_keys
category: auth-config
title: "`redis` / `scan_keys` Connection Refused on Default Port 6379 (Garnet Runs on Port 28298)"
status: documented
superseded_by: ""
supersedes: ["MCP-114"]
occurrences: 2
date: 2026-09-26
last_seen: 2026-09-26
tags: [redis, scan_keys, auth-config]
prevention_rule: "In accordance with `GEMINI.md`, prioritize TAO's native tool `kongor_cache_coherence_verify` which connects directly to Garnet on port `28298` in C# with zero external dependencies."
---

# MCP-089: `redis` / `scan_keys` Connection Refused on Default Port 6379 (Garnet Runs on Port 28298)

## Summary
Tracked MCP issue on `redis` / `scan_keys` (category: `auth-config`, status: `resolved`, occurrences: 2). Retrieve Redis keys matching `*ACCOUNT*`. -> Failed with `Error scanning keys with pattern '*ACCOUNT*': Error 111 connecting to 127.0.0.1:6379. Connection refused.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `redis` / `scan_keys` |
| **Expected** | Retrieve Redis keys matching `*ACCOUNT*`. |
| **Actual (Symptom)** | Failed with `Error scanning keys with pattern '*ACCOUNT*': Error 111 connecting to 127.0.0.1:6379. Connection refused.` |
| **Root Cause** | The external lazy-loaded `redis` MCP server connects to standard Redis port `6379`, whereas ProjectKongor runs Microsoft Garnet on port `28298`. |
| **Fix / Remediation** | In accordance with `GEMINI.md`, prioritize TAO's native tool `kongor_cache_coherence_verify` which connects directly to Garnet on port `28298` in C# with zero external dependencies. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "pattern": "*ACCOUNT*"
  }
  

Logs:

  Error scanning keys with pattern '*ACCOUNT*': Error 111 connecting to 127.0.0.1:6379. Connection refused.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server redis -Tool scan_keys
```

## Prevention Invariant
In accordance with `GEMINI.md`, prioritize TAO's native tool `kongor_cache_coherence_verify` which connects directly to Garnet on port `28298` in C# with zero external dependencies.
