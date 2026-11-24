---
id: MCP-090
server: kongor
tool: kongor_cache_coherence_verify
category: parameter-mismatch
title: "`kongor` / `kongor_cache_coherence_verify` WRONGTYPE on Non-Hash Key"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [kongor, kongor_cache_coherence_verify, parameter-mismatch]
prevention_rule: "For Redis String keys, use `action: 'get'` or inspect the key type first. Only use `hgetall` for Hash keys like `MATCH-SERVERS` and `MATCH-SERVER-MANAGERS`."
---

# MCP-090: `kongor` / `kongor_cache_coherence_verify` WRONGTYPE on Non-Hash Key

## Summary
Tracked MCP issue on `kongor` / `kongor_cache_coherence_verify` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Retrieve string session cookie value. -> Failed with `Redis error: WRONGTYPE Operation against a key holding the wrong kind of value.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_cache_coherence_verify` |
| **Expected** | Retrieve string session cookie value. |
| **Actual (Symptom)** | Failed with `Redis error: WRONGTYPE Operation against a key holding the wrong kind of value.` |
| **Root Cause** | `action: "hgetall"` issues a Redis `HGETALL` command which requires a Redis Hash data structure. `ACCOUNT-ID-SESSION-COOKIE:{id}` is stored as a Redis String (`SET`). |
| **Fix / Remediation** | For Redis String keys, use `action: "get"` or inspect the key type first. Only use `hgetall` for Hash keys like `MATCH-SERVERS` and `MATCH-SERVER-MANAGERS`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "hgetall",
    "key": "ACCOUNT-ID-SESSION-COOKIE:59"
  }
  

Logs:

  Encountered error in tool execution: Failed to execute 'hgetall' on cache server 127.0.0.1:28298: Redis error: WRONGTYPE Operation against a key holding the wrong kind of value.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_cache_coherence_verify
```

## Prevention Invariant
For Redis String keys, use `action: 'get'` or inspect the key type first. Only use `hgetall` for Hash keys like `MATCH-SERVERS` and `MATCH-SERVER-MANAGERS`.
