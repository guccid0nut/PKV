---
id: MCP-268
server: playwright
tool: browser_run_code_unsafe
category: sandbox-api
title: "browser_run_code_unsafe lacks global fetch and requires null arg before waitForFunction options"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-11
last_seen: 2026-11-11
tags: [playwright, browser_run_code_unsafe, sandbox-api]
prevention_rule: "In playwright browser_run_code_unsafe, use page.request.post(url, { data: buffer }) instead of global fetch(), and pass null as the 2nd argument to page.waitForFunction(fn, null, { timeout }) so options are not treated as pageFunction arg."
---

# MCP-268: browser_run_code_unsafe lacks global fetch and requires null arg before waitForFunction options

## Summary
When executing Playwright snippets via `playwright` / `browser_run_code_unsafe`, the sandboxed VM scope does not expose Node's global `fetch()`, and calling `page.waitForFunction(fn, { timeout: 8000 })` passes `{ timeout: 8000 }` as `arg` instead of `options`. Use `page.request.post(url, { data: buffer })` for HTTP uploads and pass `null` as the second argument (`page.waitForFunction(fn, null, { timeout: 8000 })`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `playwright` / `browser_run_code_unsafe` |
| **Expected** | Upload PNG screenshot buffers to local helper endpoint and wait with a bounded timeout for modal state transitions. |
| **Actual (Symptom)** | `ReferenceError: fetch is not defined` and `TimeoutError: page.waitForFunction: Timeout 30000ms exceeded.` |
| **Root Cause** | The Playwright MCP `vm` execution context exposes `page` (and `page.request` `APIRequestContext`) without Node's global `fetch`, and Playwright's `page.waitForFunction(pageFunction, arg, options)` signature treats the 2nd argument as `arg`. |
| **Fix / Remediation** | Replaced `fetch(...)` with `await page.request.post(url, { data: shotBuffer })` and called `await page.waitForFunction(fn, null, { timeout: 8000 })`. |

## Invocation Parameters & Raw Logs
```text
ReferenceError: fetch is not defined
TimeoutError: page.waitForFunction: Timeout 30000ms exceeded.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server playwright -Tool browser_run_code_unsafe
```

## Prevention Invariant
In playwright browser_run_code_unsafe, use page.request.post(url, { data: buffer }) instead of global fetch(), and pass null as the 2nd argument to page.waitForFunction(fn, null, { timeout }) so options are not treated as pageFunction arg.
