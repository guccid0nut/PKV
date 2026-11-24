---
id: MCP-024
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Parameter Validation Error: 'address' & 'program' Field Mismatch in `decompile_function`"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "`FastMCP.call_tool` hook normalizes `program` $\to$ `binary_name` and `address` $\to$ `name_or_address`."
---

# MCP-024: Ghidra MCP Parameter Validation Error: 'address' & 'program' Field Mismatch in `decompile_function`

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0x1809b0450` in `k2_x64.dll`. |
| **Actual (Symptom)** | Threw schema validation error: `2 validation errors for decompile_functionArguments: binary_name: Field required, name_or_address: Field required`. |
| **Root Cause** | The Ghidra MCP schema requires `binary_name` and `name_or_address` as parameter names instead of `program` and `address`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `program` $\to$ `binary_name` and `address` $\to$ `name_or_address` before schema validation executes. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "program": "k2_x64.dll",
    "address": "0x1809b0450"
  }
  

Logs:

  Error executing tool decompile_function: 2 validation errors for decompile_functionArguments
  binary_name: Field required
  name_or_address: Field required
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `program` $\to$ `binary_name` and `address` $\to$ `name_or_address`.
