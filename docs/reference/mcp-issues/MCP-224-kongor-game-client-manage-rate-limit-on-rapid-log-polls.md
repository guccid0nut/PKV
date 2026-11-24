---
id: MCP-224
server: kongor
tool: kongor_game_client_manage
category: rate-limit
title: "KONGOR-GAME-CLIENT-MANAGE-RATE-LIMIT-ON-RAPID-LOG-POLLS"
status: superseded
superseded_by: "MCP-138"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [kongor, kongor_game_client_manage, rate-limit]
prevention_rule: "Never use `kongor_game_client_manage` (`action: 'logs'`) to read `console.log`; read `games/client/userdata/Heroes of Newerth x64/override/console.log` directly via `view_file` or use `kongor_get_client_logs` (`Diagnostics` category) so `Se..."
---

# MCP-224: KONGOR-GAME-CLIENT-MANAGE-RATE-LIMIT-ON-RAPID-LOG-POLLS

## Summary
Repeat occurrence of [`MCP-138`] on `kongor` / `kongor_game_client_manage` (`rate-limit`). Superseded by `MCP-138`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Execute console command on `hon_x64.exe` (`PID 87760`). |
| **Actual (Symptom)** | Returned `Rate limit exceeded for 'ServerManagement' category. Tool 'kongor_game_client_manage' was blocked to prevent runaway loops. Try again in 60 seconds.` |
| **Root Cause** | `kongor_game_client_manage` shares the `ServerManagement` rate-limit bucket across `status`, `command`, and `logs` actions. Calling `action: "logs"` repeatedly after `action: "command"` exhausted the per-minute budget. |
| **Fix / Remediation** | Never use `kongor_game_client_manage` (`action: "logs"`) to read `console.log`; read `games/client/userdata/Heroes of Newerth x64/override/console.log` directly via `view_file` or use `kongor_get_client_logs` (`Diagnostics` category) so `ServerManagement` budget is reserved for lifecycle/command actions. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "command", "command": "con_flushLog true; Echo TEST_FLUSH"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Never use `kongor_game_client_manage` (`action: 'logs'`) to read `console.log`; read `games/client/userdata/Heroes of Newerth x64/override/console.log` directly via `view_file` or use `kongor_get_client_logs` (`Diagnostics` category) so `Se...
