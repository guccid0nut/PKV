---
id: MCP-294
server: kongor
tool: kongor_call_tool
category: error-contract
title: "Inconsistent result envelope and misattributed remediation hints"
status: resolved
verified_by: "1ebe5f40d + McpToolContractTests.Tranche1A + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_call_tool, error-contract]
prevention_rule: "Every tool returns one envelope (status, isError, errorCode, message, remediation) and attaches an MCP issue only when the error signature matches it."
---

# MCP-294: Inconsistent result envelope and misattributed remediation hints

## Summary
`kongor` tools do not share one result contract. The meaning of `isError`, `status` and `success` differs per tool, some errors are plain text, the remediation hint is almost always `kongor_health_check`, and unrelated MCP issue IDs are attached to errors. An agent cannot decide from the envelope alone whether a call worked. Found in the 2026-11-17 live audit (503 calls, 93 tools). Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_call_tool` |
| **Expected** | One envelope for every tool: `status`, `isError`, `errorCode`, `message`, `remediation`, with `isError` true only when the tool could not do its job. |
| **Actual (Symptom)** | Findings reported as errors: `kongor_vfs_completeness_audit` and `kongor_validate_previews` set `isError: true` when they find problems, while `kongor_k2_lint`, `kongor_inspect_hero_pool` and `kongor_sync_mirrors` do not. Failures reported as success: `kongor_assert_ui` on a missing widget has no `status` and `isError: false`; `kongor_ui_flow click_widget` on a missing widget returns `status: success`. No `status` field at all: `kongor_tao_capabilities`, `kongor_rate_limit_status`, `kongor_native_manage`, `kongor_diagnostics`, `kongor_crash_dump_analyze list_recent`. Plain-text errors: rate limit, `kongor_realtime_stream`, `kongor_db_fixture`, `kongor_net_packet_fuzz`. `kongor_log_correlate` returns a markdown string. Wrong attribution: an unknown tool name via `kongor_call_tool` carried `MCP-277`; a rate-limit error carried `MCP-232`; a Lua syntax error suggested `kongor_health_check`. `kongor_realtime_stream subscribe` leaked an unhandled .NET format exception. Offline tools still report `executionFidelity: LiveVerified` (`MCP-277`, `MCP-279`). |
| **Root Cause** | Each handler builds its own response object. Issue enrichment (`MatchIssueForError`) appears to match on the tool name rather than on the error signature; this was not traced in source. |
| **Fix / Remediation** | Not fixed. Until fixed, clients must inspect `isError`, `status`, `success`, `verified`, `passed` and nested `status` fields together. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 147, 148, 159 completeness_audit / validate_previews -> isError true for findings
row 97 kongor_assert_ui {widget: no_such_widget_xyz}       -> no status, isError false, passed false
row 116 kongor_ui_flow {click_widget, missing widget}     -> status success, verified false
row 229 kongor_call_tool {call, kongor_no_such_tool}      -> mcpIssueId MCP-277
row 314 kongor_client_practice {status}                   -> rate limit text with MCP-232
row 17 kongor_client_eval_lua {code: this is not lua}     -> errorClass caller_lua_error, remediation kongor_health_check
row 282 kongor_realtime_stream {subscribe}                -> Format string can be only "D", "d", "N", "n", "P", "p", "B", "b", "X" or "x"
row 220 kongor_log_correlate {errors, minutes: 5}         -> markdown string, 1368 entries from an 18 h old log
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_call_tool
```

## Prevention Invariant
Every tool returns one envelope (status, isError, errorCode, message, remediation) and attaches an MCP issue only when the error signature matches it.
