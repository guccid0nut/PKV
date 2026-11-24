---
id: MCP-272
server: docker
tool: list_containers
category: transport-disconnect
title: "mcp-server-docker stale named pipe handle after Docker Desktop engine restart"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-14
last_seen: 2026-11-14
tags: [docker, list_containers, transport-disconnect]
prevention_rule: "After restarting Docker Desktop or WSL2 on Windows, mcp-server-docker.exe caches a closed //./pipe/dockerDesktopLinuxEngine handle; fall back to docker CLI or restart mcp-server-docker before calling docker MCP tools."
---

# MCP-272: mcp-server-docker stale named pipe handle after Docker Desktop engine restart

## Summary
One-paragraph builder summary naming the MCP server, tool, invocation context, failure signature, and remediation status.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `docker` / `list_containers` |
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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server docker -Tool list_containers
```

## Prevention Invariant
After restarting Docker Desktop or WSL2 on Windows, mcp-server-docker.exe caches a closed //./pipe/dockerDesktopLinuxEngine handle; fall back to docker CLI or restart mcp-server-docker before calling docker MCP tools.
