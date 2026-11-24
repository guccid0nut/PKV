---
id: MCP-253
server: kongor
tool: kongor_diagnostics
category: false-positive
title: "Reports overallStatus degraded solely when optional dotnet-monitor 52323 is offline"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-01
last_seen: 2026-11-01
tags: [kongor, kongor_diagnostics, false-positive]
prevention_rule: "Treat optional dotnet-monitor (127.0.0.1:52323) offline as informational rather than degrading overallStatus when Redis, Database, MasterServer, ChatServer, Web API, Web UI, and COMPEL are healthy."
---

# MCP-253: Reports overallStatus degraded solely when optional dotnet-monitor 52323 is offline

## Summary
Calling `kongor` / `kongor_diagnostics` against a fully healthy Aspire backend cluster reported `overallStatus: "degraded"` solely because the optional `dotnet-monitor` sidecar endpoint (`127.0.0.1:52323`) was not running, even though PostgreSQL, Redis, MasterServer (`55555`), ChatServer (`11031`), Web API, Web UI, and COMPEL were all online and passing health probes. Tracked as `open` so callers inspect individual core service health flags rather than treating `dotnet-monitor` absence as a cluster outage.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_diagnostics` |
| **Expected** | Report `overallStatus: "healthy"` whenever all required Aspire gameplay and persistence services (PostgreSQL, Redis, MasterServer, ChatServer, COMPEL) pass health probes, surfacing optional profiling sidecars (`dotnet-monitor`) as informational metadata. |
| **Actual (Symptom)** | Returns `"overallStatus": "degraded"` with `"dotnetMonitor": {"reachable": false, "endpoint": "http://127.0.0.1:52323"}` while all gameplay services report `"healthy": true`. |
| **Root Cause** | `DiagnosticsHandler` / `ClusterDiagnosticsService` (`src/tools/kongor-cli/Mcp/Services/ClusterDiagnosticsService.cs`) aggregates optional telemetry sidecars (`dotnet-monitor` on TCP `52323`) into the top-level `allHealthy` boolean with the same weight as core services (`MasterServer`, `ChatServer`, `PostgreSQL`, `Redis`), downgrading `overallStatus` to `"degraded"` in standard local dev sessions where `dotnet-monitor` is disabled. |
| **Fix / Remediation** | 1. Classify `dotnet-monitor` (`127.0.0.1:52323`) as `optional: true` in `ClusterDiagnosticsService` so `overallStatus` remains `"healthy"` when all core services are up.<br>2. Until patched, evaluate per-service health fields (`redis`, `database`, `masterServer`, `chatServer`, `compel`) in the `kongor_diagnostics` response. |

## Invocation Parameters & Raw Logs
```json
{
  "includeCluster": true
}
```
```json
{
  "overallStatus": "degraded",
  "services": {
    "postgres": { "healthy": true },
    "redis": { "healthy": true },
    "masterServer": { "healthy": true, "port": 55555 },
    "chatServer": { "healthy": true, "port": 11031 },
    "compel": { "healthy": true },
    "dotnetMonitor": { "healthy": false, "port": 52323, "optional": true }
  }
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_diagnostics
```

## Prevention Invariant
Treat optional `dotnet-monitor` (`127.0.0.1:52323`) offline as informational rather than degrading `overallStatus` when Redis, Database, MasterServer, ChatServer, Web API, Web UI, and COMPEL are healthy.
