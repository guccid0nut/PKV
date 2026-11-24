---
id: MCP-013
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Parameter Validation Error: Address Field Mismatch in `decompile_function`"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`."
---

# MCP-013: Ghidra MCP Parameter Validation Error: Address Field Mismatch in `decompile_function`

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0x1808d5a80`. |
| **Actual (Symptom)** | Failed with schema validation error: `Field required: name_or_address`. |
| **Root Cause** | The Ghidra MCP server tool schema defines `name_or_address` as the parameter for function name or address, not `address`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `address`, `symbol`, `addr`, etc. to `name_or_address` prior to schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "address": "0x1808d5a80"
  }
  

Logs:

  Error executing tool decompile_function: 1 validation error for decompile_functionArguments
  name_or_address
    Field required [type=missing, input_value={'address': '0x1808d5a80'...ary_name': 'k2_x64.dll'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `address` $\to$ `name_or_address`.
