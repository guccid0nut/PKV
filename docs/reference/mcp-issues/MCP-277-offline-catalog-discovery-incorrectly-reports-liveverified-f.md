---
id: MCP-277
server: kongor
tool: kongor_call_tool
category: execution-fidelity
title: "Offline catalog discovery incorrectly reports LiveVerified fidelity"
status: resolved
codified_in: .agents/rules/mcp_issues.md
verified_by: "1ebe5f40d + McpToolContractTests.Tranche1A + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_call_tool, execution-fidelity]
prevention_rule: "Use StaticOfflineAnalysis for offline catalog list, search and describe; reserve LiveVerified for verified in-engine execution."
---

# MCP-277: Offline catalog discovery incorrectly reports LiveVerified fidelity

## Summary
With the game client offline, `kongor_call_tool` list, search and describe return `executionMode: offline_direct` but incorrectly claim `executionFidelity: LiveVerified`. `kongor_enable_toolset` does the same for a successful offline UI toolset expansion. This can mislead consumers into treating offline operations as live engine verification. Open; no application fix has been applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_call_tool` |
| **Expected** | Offline catalog operations identify their fidelity as offline, without asserting in-engine live verification. |
| **Actual (Symptom)** | List, search and describe return `executionMode: offline_direct`, `executionFidelity: LiveVerified`, and `targetStatus: null` while health reports the client offline. |
| **Root Cause** | `src/tools/kongor-cli/Mcp/Handlers/CallToolHandler.cs` explicitly sets `ExecutionFidelity.LiveVerified` in both catalog and describe response builders; `EnableToolsetHandler.cs` does likewise. |
| **Fix / Remediation** | Not fixed. Until corrected, treat these discovery responses as offline metadata, not proof of a live engine check. |

## Invocation Parameters & Raw Logs
```text
kongor_health_check: {"status":"offline","healthy":false,"checks":{"client":{"running":false}}}
kongor_call_tool: {"action":"search","query":"vfs_resolve"}
response: {"status":"success","executionMode":"offline_direct","executionFidelity":"LiveVerified","targetStatus":null}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_call_tool
```

## Prevention Invariant
Use StaticOfflineAnalysis for offline catalog list, search and describe; reserve LiveVerified for verified in-engine execution.
