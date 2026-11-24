---
id: MCP-032
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Parameter Validation Error: `name_or_address` Field Required in `decompile_function`"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`."
---

# MCP-032: Ghidra MCP Parameter Validation Error: `name_or_address` Field Required in `decompile_function`

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0x1808e8ea0`. |
| **Actual (Symptom)** | Returned error: `1 validation error for decompile_functionArguments: name_or_address: Field required`. |
| **Root Cause** | `decompile_function` expects `name_or_address`, not `address`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `address` $\to$ `name_or_address` before schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "address": "1808e8ea0"
  }
  

Logs:

  Error executing tool decompile_function: 1 validation error for decompile_functionArguments
  name_or_address
    Field required [type=missing, input_value={'address': '1808e8ea0', 'binary_name': 'k2_x64.dll'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`.
