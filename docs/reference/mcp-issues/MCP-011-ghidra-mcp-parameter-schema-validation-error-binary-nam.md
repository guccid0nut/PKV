---
id: MCP-011
server: ghidra
tool: list_xrefs
category: binary-decode
title: "Ghidra MCP Parameter Schema Validation Error (binary_name and name_or_address)"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-219"]
occurrences: 9
date: 2026-09-24
last_seen: 2026-11-15
tags: [ghidra, list_xrefs, binary-decode]
prevention_rule: "`FastMCP.call_tool` hook aliases `program` $\to$ `binary_name` and `address` $\to$ `name_or_address`."
---

# MCP-011: Ghidra MCP Parameter Schema Validation Error (binary_name and name_or_address)

## Summary
Tracked MCP issue on `ghidra` / `list_xrefs` (category: `binary-decode`, status: `codified`, occurrences: 9). Return cross-references to the specified virtual address in `k2_x64.dll`. -> Threw 2 validation errors: `binary_name: Field required`, `name_or_address: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | Return cross-references to the specified virtual address in `k2_x64.dll`. |
| **Actual (Symptom)** | Threw 2 validation errors: `binary_name: Field required`, `name_or_address: Field required`. |
| **Root Cause** | The Ghidra MCP tools standardize parameter names as `binary_name` (not `program`) and `name_or_address` (not `address`). |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook dynamically intercepts `list_xrefs` and aliases `program` $\to$ `binary_name` and `address` $\to$ `name_or_address` before Pydantic schema validation executes. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "program": "k2_x64.dll",
    "address": "0x1812b87c9"
  }
  

Logs:

  Error executing tool list_xrefs: 2 validation errors for list_xrefsArguments
  binary_name: Field required
  name_or_address: Field required
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
`FastMCP.call_tool` hook aliases `program` $\to$ `binary_name` and `address` $\to$ `name_or_address`.
