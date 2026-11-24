---
id: MCP-022
server: ghidra
tool: list_xrefs
category: parameter-mismatch
title: "Ghidra MCP Parameter Validation Error: 'address' Field Mismatch in `list_xrefs`"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-037", "MCP-040", "MCP-043", "MCP-047"]
occurrences: 13
date: 2026-09-24
last_seen: 2026-11-10
tags: [ghidra, list_xrefs, parameter-mismatch]
prevention_rule: "`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`."
---

# MCP-022: Ghidra MCP Parameter Validation Error: 'address' Field Mismatch in `list_xrefs`

## Summary
Tracked MCP issue on `ghidra` / `list_xrefs` (category: `parameter-mismatch`, status: `investigating`, occurrences: 13). Retrieve xrefs to address `0x1808e5c60`. -> Threw schema validation error: `1 validation error for list_xrefsArguments: name_or_address: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | Retrieve xrefs to address `0x1808e5c60`. |
| **Actual (Symptom)** | Threw schema validation error: `1 validation error for list_xrefsArguments: name_or_address: Field required`. |
| **Root Cause** | The Ghidra MCP server tool schema defines `name_or_address` as the parameter for `list_xrefs`, not `address`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "address": "0x1808e5c60"
  }
  

Logs:

  Error executing tool list_xrefs: 1 validation error for list_xrefsArguments
  name_or_address
    Field required [type=missing, input_value={'address': '0x1808e5c60', 'binary_name': 'k2_x64.dll'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`.
