---
id: MCP-269
server: playwright
tool: browser_run_code_unsafe
category: screenshot-timeout
title: "page.screenshot times out waiting on infinite CSS animations or Google Fonts"
status: investigating
verified_by: ""
codified_in: ".agents/rules/mcp_issues.md"
superseded_by: ""
supersedes: []
occurrences: 8
date: 2026-11-11
last_seen: 2026-11-20
tags: [playwright, browser_run_code_unsafe, screenshot-timeout]
prevention_rule: "Pass animations: 'disabled' (and timeout: 10000) to page.screenshot({ fullPage: false, animations: 'disabled' }) on pages with continuous CSS animations or blocked external font requests."
---

# MCP-269: page.screenshot times out waiting on infinite CSS animations or Google Fonts

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `playwright` / `browser_run_code_unsafe` |
| **Expected** | What the tool call was expected to return or execute. |
| **Actual (Symptom)** | Exact error message, timeout duration, or unexpected response returned. |
| **Root Cause** | Technical explanation of why the MCP tool or transport failed. |
| **Fix / Remediation** | Code patch, wrapper normalization, or workflow fallback applied. |

## Invocation Parameters & Raw Logs
```text
<Exact JSON parameters passed and verbatim error log / stderr / NDJSON snippet>
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server playwright -Tool browser_run_code_unsafe
```

## Prevention Invariant
Pass animations: 'disabled' (and timeout: 10000) to page.screenshot({ fullPage: false, animations: 'disabled' }) on pages with continuous CSS animations or blocked external font requests.
