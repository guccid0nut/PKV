---
id: MCP-292
server: kongor
tool: kongor_client_practice
category: rate-limit
title: "Simulation rate limit blocks read-only status and dry-run calls"
status: resolved
verified_by: "1ebe5f40d + McpToolContractTests.Tranche1A + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_client_practice, rate-limit]
prevention_rule: "Rate-limit by action cost: status, list, errors and dryRun actions are billed to ReadOnly, never to the Simulation budget."
---

# MCP-292: Simulation rate limit blocks read-only status and dry-run calls

## Summary
The `Simulation` rate-limit category allows 2 calls per 60 s and is charged for read-only actions. `status`, `list`, `errors` and `dryRun` calls of `kongor_client_practice`, `kongor_scrimmage_launch`, `kongor_match_launch` and `kongor_bot_choreography` were blocked after two calls. Found in the 2026-11-17 live audit. Open; no fix applied. Same class as `MCP-237`, which covered `kongor_game_client_manage` only.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_practice` |
| **Expected** | Read-only actions are charged to `ReadOnly` (60 per 60 s). The limit error is structured JSON with a retry time. |
| **Actual (Symptom)** | Plain-text error: `Rate limit exceeded for 'Simulation' category. Tool '...' was blocked to prevent runaway loops. Try again in 60 seconds.` A `dryRun` plus one `launch` of `kongor_client_practice` exhausted the budget, so the following `status` failed. For `kongor_client_practice` the error text also carried `[mcpIssueId: MCP-232]` and that issue's unrelated prevention text. |
| **Root Cause** | The limiter is keyed by tool, not by action, and these tools are mapped to `Simulation` (limit 2, period 60 s per `kongor_rate_limit_status`). The mapping code was not inspected in this audit. |
| **Fix / Remediation** | Not fixed. Until fixed, space calls to these four tools at least 30 s apart and do not use `dryRun` before a real launch. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
row 223 kongor_rate_limit_status -> Simulation: limit 2, periodSeconds 60
rows 209-212 kongor_scrimmage_launch {status / list / errors}, kongor_match_launch {status} -> blocked
row 314 kongor_client_practice {status} -> blocked after dryRun (312) and launch (313); text includes [mcpIssueId: MCP-232]
rows 387, 388 kongor_bot_choreography {execute / stop} -> blocked
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_practice
```

## Prevention Invariant
Rate-limit by action cost: status, list, errors and dryRun actions are billed to ReadOnly, never to the Simulation budget.
