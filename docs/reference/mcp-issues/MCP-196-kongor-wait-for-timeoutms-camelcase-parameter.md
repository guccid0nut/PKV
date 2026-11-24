---
id: MCP-196
server: kongor
tool: kongor_wait_for
category: ipc-timeout
title: "KONGOR-WAIT-FOR-TIMEOUTMS-CAMELCASE-PARAMETER"
status: resolved
verified_by: "McpToolContractTests.MCP194_And_MCP196_WaitForHandler_ColdBootTimeoutDefault_AndTimeoutParameterAliases"
superseded_by: ""
supersedes: ["MCP-201", "MCP-207"]
occurrences: 4
date: 2026-10-13
last_seen: 2026-10-16
tags: [kongor, kongor_wait_for, ipc-timeout]
prevention_rule: "Pass `'timeoutMs': 25000` (camelCase) when calling `kongor_wait_for`."
---

# MCP-196: KONGOR-WAIT-FOR-TIMEOUTMS-CAMELCASE-PARAMETER

## Summary
Tracked MCP issue on `kongor` / `kongor_wait_for` (category: `ipc-timeout`, status: `resolved`, occurrences: 3). Wait up to 25,000ms for `engine_ready`. -> Used default `timeoutMs: 5000` (`elapsedMs: 7495.41`) because `WaitForHandler` parses `timeoutMs` (camelCase) before `timeout_ms` on the running pre-Step-11 MCP server instance.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | Wait up to 25,000ms for `engine_ready`. |
| **Actual (Symptom)** | Used default `timeoutMs: 5000` (`elapsedMs: 7495.41`) because `WaitForHandler` parses `timeoutMs` (camelCase) before `timeout_ms` on the running pre-Step-11 MCP server instance. |
| **Root Cause** | `WaitForHandler` in the active MCP server process parsed `timeoutMs` (camelCase), defaulting to 5,000ms when `timeout_ms` was passed. |
| **Fix / Remediation** | Pass `"timeoutMs": 25000` (camelCase) when calling `kongor_wait_for`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"condition": "engine_ready", "timeout_ms": 25000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
Pass `'timeoutMs': 25000` (camelCase) when calling `kongor_wait_for`.
