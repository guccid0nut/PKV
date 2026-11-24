---
id: MCP-249
server: kongor
tool: kongor_matchmaking_simulate
category: tool_fault
title: "Virtual client handshake rejected with 0x02 due to missing auth cookies"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-26
last_seen: 2026-10-26
tags: [kongor, kongor_matchmaking_simulate, tool_fault]
prevention_rule: "Seed session auth keys in Redis before running virtual matchmaking simulation"
---

# MCP-249: Virtual client handshake rejected with 0x02 due to missing auth cookies

## Summary
Invoking `kongor` / `kongor_matchmaking_simulate` to drive synthetic 10-player TMM queue formation against a live ChatServer (`127.0.0.1:11031`) failed during the virtual client TCP handshake with rejection code `0x02` (`AUTH_FAILED`) because synthetic bot accounts did not have active 20-character session cookies seeded in Redis / MasterServer session storage prior to connecting. Tracked as `open` with a pre-flight requirement to authenticate synthetic accounts via MasterServer or seed session cookies before launching virtual TMM clients.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_matchmaking_simulate` |
| **Expected** | Connect synthetic player clients to ChatServer, authenticate each virtual client, enter the TMM queue, and form a balanced match roster. |
| **Actual (Symptom)** | Virtual clients connect to TCP `11031` and immediately receive server disconnect / handshake rejection `0x02` (`Invalid session cookie`) with `0` players entering the queue. |
| **Root Cause** | `MatchmakingSimulateHandler` (`src/tools/kongor-cli/Mcp/Handlers/MatchmakingSimulateHandler.cs`) synthesizes placeholder cookies when `autoSeedSessions` / MasterServer login pre-seeding is skipped, whereas `ChatServer` validates every incoming `0xC00` handshake cookie against Redis/database session state and rejects unseeded cookies with `0x02`. |
| **Fix / Remediation** | 1. Seed valid session cookies via `kongor_db_fixture` or MasterServer `client_requester.php?f=auth` before opening virtual ChatServer sockets in `MatchmakingSimulateHandler`.<br>2. Verify Redis is online (`kongor_diagnostics`) before invoking `kongor_matchmaking_simulate`. |

## Invocation Parameters & Raw Logs
```json
{
  "playerCount": 10,
  "region": "USW",
  "mode": "normal",
  "timeoutMs": 15000
}
```
```json
{
  "status": "error",
  "connectedClients": 0,
  "rejectedClients": 10,
  "error": "ChatServer handshake rejected virtual client 'SimBot_01' with code 0x02 (missing or invalid session cookie)."
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_matchmaking_simulate
```

## Prevention Invariant
Seed session auth keys in Redis / MasterServer before running virtual matchmaking simulation via `kongor_matchmaking_simulate`.
