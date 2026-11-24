---
id: MCP-187
server: sqlite
tool: sqlite
category: parameter-mismatch
title: "DOCKER-MCP-SERVER-DISCOVER-VALIDATION-ERROR (MCP-2026-10-06)"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [sqlite, parameter-mismatch]
prevention_rule: "Ensure containerized Python MCP servers use `mcp>=2.1.0` to maintain compatibility with modern IDE capability discovery handshakes."
---

# MCP-187: DOCKER-MCP-SERVER-DISCOVER-VALIDATION-ERROR (MCP-2026-10-06)

## Summary
Tracked MCP issue on `sqlite` / `sqlite` (category: `parameter-mismatch`, status: `patched`, occurrences: 1). Successful capability exchange and ready state. -> Crash with `ValidationError: 24 validation errors for ClientRequest ... Input should be 'initialize' [type=literal_error, input_value='server/discover', input_type=str]`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `sqlite` / `sqlite` |
| **Expected** | Successful capability exchange and ready state. |
| **Actual (Symptom)** | Crash with `ValidationError: 24 validation errors for ClientRequest ... Input should be 'initialize' [type=literal_error, input_value='server/discover', input_type=str]`. |
| **Root Cause** | Ancient Docker images (15 months old) were built with legacy Python MCP SDK 1.x using strict discriminated unions that rejected IDE discovery methods (`server/discover`). |
| **Fix / Remediation** | Ensure containerized Python MCP servers use `mcp>=2.1.0` to maintain compatibility with modern IDE capability discovery handshakes. |

## Invocation Parameters & Raw Logs
```text
Parameters:
Initial handshake from Antigravity IDE
* **Expected**: Successful capability exchange and ready state.
* **Actual**: Crash with `ValidationError: 24 validation errors for ClientRequest ... Input should be 'initialize' [type=literal_error, input_value='server/discover', input_type=str]`.
* **Root Cause**: Ancient Docker images (15 months old) were built with legacy Python MCP SDK 1.x using strict discriminated unions that rejected IDE discovery methods (`server/discover`).
* **Status**: `[PATCHED]` (Rebuilt `mcp/sqlite:latest` and `mcp/git:latest` with modern `mcp 2.3.0` which properly validates and responds to `server/discover` without closing the stdio transport).
* **Next-Time Prevention Rule**: Ensure containerized Python MCP servers use `mcp>=2.1.0` to maintain compatibility with modern IDE capability discovery handshakes.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server sqlite -Tool sqlite
```

## Prevention Invariant
Ensure containerized Python MCP servers use `mcp>=2.1.0` to maintain compatibility with modern IDE capability discovery handshakes.
