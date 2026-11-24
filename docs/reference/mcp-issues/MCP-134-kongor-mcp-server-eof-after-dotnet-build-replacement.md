---
id: MCP-134
server: kongor
tool: kongor_health_check
category: transport-eof
title: "KONGOR-MCP-SERVER-EOF-AFTER-DOTNET-BUILD-REPLACEMENT"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-12
last_seen: 2026-10-12
tags: [kongor, kongor_health_check, transport-eof]
prevention_rule: "Patched in Step 2 (`src/tools/kongor-mcp-wrapper.py`): `kongor-mcp-wrapper.py` copies `bin/Release/net9.0` to a per-PID shadow directory before launching `dotnet kongor.dll --mcp`, eliminating `MSB3021`/`MSB3027` file locks and stdio EOF cr..."
---

# MCP-134: KONGOR-MCP-SERVER-EOF-AFTER-DOTNET-BUILD-REPLACEMENT

## Summary
Tracked MCP issue on `kongor` / `kongor_health_check` (category: `transport-eof`, status: `patched`, occurrences: 1). Tool call handled via in-process lazy stdio MCP host. -> `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_health_check` |
| **Expected** | Tool call handled via in-process lazy stdio MCP host. |
| **Actual (Symptom)** | `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | Rebuilding `Kongor.Cli.csproj` replaced `kongor.dll` on disk while the background MCP server process was running, causing the stdio host to terminate on assembly file access / process recycling. |
| **Fix / Remediation** | Patched in Step 2 (`src/tools/kongor-mcp-wrapper.py`): `kongor-mcp-wrapper.py` copies `bin/Release/net9.0` to a per-PID shadow directory before launching `dotnet kongor.dll --mcp`, eliminating `MSB3021`/`MSB3027` file locks and stdio EOF crashes during rebuilds. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_health_check
```

## Prevention Invariant
Patched in Step 2 (`src/tools/kongor-mcp-wrapper.py`): `kongor-mcp-wrapper.py` copies `bin/Release/net9.0` to a per-PID shadow directory before launching `dotnet kongor.dll --mcp`, eliminating `MSB3021`/`MSB3027` file locks and stdio EOF cr...
