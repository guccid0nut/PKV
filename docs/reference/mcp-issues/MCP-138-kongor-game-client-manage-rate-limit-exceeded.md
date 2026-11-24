---
id: MCP-138
server: kongor
tool: kongor_game_client_manage
category: rate-limit
title: "KONGOR-GAME-CLIENT-MANAGE-RATE-LIMIT-EXCEEDED"
status: resolved
verified_by: "McpToolContractTests.MCP138_And_MCP141_And_MCP142_RateLimitIsolation_NativePipeNoFallback_AndGracefulClientStatus"
superseded_by: ""
supersedes: ["MCP-224"]
occurrences: 4
date: 2026-10-13
last_seen: 2026-10-18
tags: [kongor, kongor_game_client_manage, rate-limit]
prevention_rule: "Use `kongor_wait_for` (`condition: 'engine_ready'`) to wait for client startup rather than polling `kongor_game_client_manage` (`action: 'status'`) in a loop, preserving the `ServerManagement` rate-limit budget for lifecycle actions (`launc..."
---

# MCP-138: KONGOR-GAME-CLIENT-MANAGE-RATE-LIMIT-EXCEEDED

## Summary
Tracked MCP issue on `kongor` / `kongor_game_client_manage` (category: `rate-limit`, status: `resolved`, occurrences: 2). Restart `hon_x64.exe` immediately after rapid `status` / `errors` polling. -> Returned `Rate limit exceeded for 'ServerManagement' category. Please wait 35 seconds before retrying. (Limit: 5 requests per 60s)`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Restart `hon_x64.exe` immediately after rapid `status` / `errors` polling. |
| **Actual (Symptom)** | Returned `Rate limit exceeded for 'ServerManagement' category. Please wait 35 seconds before retrying. (Limit: 5 requests per 60s)`. |
| **Root Cause** | `kongor_game_client_manage` shares the `ServerManagement` rate-limit bucket (5 requests per 60s). Calling `status` and `errors` multiple times immediately after `launch` exhausted the 5-request window before `restart` was invoked. |
| **Fix / Remediation** | Use `kongor_wait_for` (`condition: "engine_ready"`) to wait for client startup rather than polling `kongor_game_client_manage` (`action: "status"`) in a loop, preserving the `ServerManagement` rate-limit budget for lifecycle actions (`launch`, `restart`, `stop`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "restart", "window_mode": "foreground"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Use `kongor_wait_for` (`condition: 'engine_ready'`) to wait for client startup rather than polling `kongor_game_client_manage` (`action: 'status'`) in a loop, preserving the `ServerManagement` rate-limit budget for lifecycle actions (`launc...
