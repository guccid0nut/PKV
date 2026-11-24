---
id: MCP-270
server: playwright
tool: browser_tabs
category: container-daemon-unavailable
title: "playwright MCP fails with exit status 125 when Docker Desktop daemon is restarting or stopped"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-11
last_seen: 2026-11-11
tags: [playwright, browser_tabs, container-daemon-unavailable]
prevention_rule: "Verify Docker Desktop daemon health before invoking Docker-backed playwright MCP; if Docker daemon is restarting or unavailable, wait for com.docker.backend readiness or run local host Playwright."
---

# MCP-270: playwright MCP fails with exit status 125 when Docker Desktop daemon is restarting or stopped

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `playwright` / `browser_tabs` |
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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server playwright -Tool browser_tabs
```

## Prevention Invariant
Verify Docker Desktop daemon health before invoking Docker-backed playwright MCP; if Docker daemon is restarting or unavailable, wait for com.docker.backend readiness or run local host Playwright.
