---
id: MCP-143
server: kongor
tool: unknown
category: binary-decode
title: "[2026-09-29 02:16:30] Redis MCP Server `hgetall` UTF-8 Decode Failure on Binary Key"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-29
last_seen: 2026-09-29
tags: [kongor, binary-decode]
prevention_rule: "For binary serialized hashes or caches in Redis/Garnet, inspect via custom C# scripts or REST API endpoints from MasterServer / COMPEL rather than calling raw Redis string MCP tools."
---

# MCP-143: [2026-09-29 02:16:30] Redis MCP Server `hgetall` UTF-8 Decode Failure on Binary Key

## Summary
Tracked MCP issue on `kongor` / `unknown` (category: `binary-decode`, status: `resolved`, occurrences: 1). - -> -

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `unknown` |
| **Expected** | - |
| **Actual (Symptom)** | - |
| **Root Cause** | The Redis MCP server implementation assumes all hash field values are UTF-8 text strings. Keys such as `MATCH-SERVERS` contain binary serialized structures (e.g. MessagePack/Protobuf), causing Python's UTF-8 decoder in the MCP server to throw `UnicodeDecodeError: 'utf-8' codec can't decode byte 0xf0 in position 5: invalid continuation byte`. |
| **Fix / Remediation** | Query state via Master Server REST API and supervisor health logs. |

## Invocation Parameters & Raw Logs
```text
Encountered error in tool execution: Error executing tool hgetall: 'utf-8' codec can't decode byte 0xf0 in position 5: invalid continuation byte
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool unknown
```

## Prevention Invariant
For binary serialized hashes or caches in Redis/Garnet, inspect via custom C# scripts or REST API endpoints from MasterServer / COMPEL rather than calling raw Redis string MCP tools.
