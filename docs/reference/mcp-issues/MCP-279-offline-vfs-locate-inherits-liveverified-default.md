---
id: MCP-279
server: kongor
tool: kongor_vfs_locate
category: execution-fidelity
title: "Offline VFS locate inherits LiveVerified default"
status: resolved
verified_by: "1ebe5f40d + McpToolContractTests.Tranche1A + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_vfs_locate, execution-fidelity]
prevention_rule: "Offline handlers must specify StaticOfflineAnalysis; avoid defaulting execution fidelity to LiveVerified."
---

# MCP-279: Offline VFS locate inherits LiveVerified default

## Summary
`kongor_vfs_locate` successfully locates a file offline but claims live verification while the game client is stopped. This is a separate cause from `MCP-277`, where the catalog explicitly supplies the wrong fidelity. Open; no application fix has been applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_locate` |
| **Expected** | A disk-only lookup is identified as offline analysis, not a live engine verification. |
| **Actual (Symptom)** | `executionMode: offline_direct`, `executionFidelity: LiveVerified`, and `targetStatus: null` are returned while the client is offline. The path itself resolved to the override layer. |
| **Root Cause** | `VfsLocateHandler.cs` calls `ExecutionTruthEnvelope.CreateJsonResult` without a fidelity argument; `ExecutionTruthEnvelope.cs` defaults it to `LiveVerified`. |
| **Fix / Remediation** | Not fixed. Treat the response as an offline disk lookup until fidelity handling is corrected. Audit other callers that omit fidelity. |

## Invocation Parameters & Raw Logs
```text
kongor_vfs_locate: virtual_path=/ui/scripts/store2.lua
response: status=success, executionMode=offline_direct, executionFidelity=LiveVerified, targetStatus=null, winningLayer=override
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_locate
```

## Prevention Invariant
Offline handlers must specify StaticOfflineAnalysis; avoid defaulting execution fidelity to LiveVerified.
