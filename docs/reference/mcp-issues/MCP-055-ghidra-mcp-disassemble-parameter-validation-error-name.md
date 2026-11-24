---
id: MCP-055
server: ghidra
tool: disassemble
category: decompiler-timeout
title: "Ghidra MCP disassemble Parameter Validation Error ('name_or_address' instead of 'address')"
status: superseded
superseded_by: "MCP-033"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, disassemble, decompiler-timeout]
prevention_rule: "Patched `ghidra/mcp_server.py` normalizes and pops `name_or_address` $\to$ `address`."
---

# MCP-055: Ghidra MCP disassemble Parameter Validation Error ('name_or_address' instead of 'address')

## Summary
Repeat occurrence of [`MCP-033`] on `ghidra` / `disassemble` (`decompiler-timeout`). Superseded by `MCP-033`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `disassemble` |
| **Expected** | Disassemble machine instructions starting at `180ab7180`. |
| **Actual (Symptom)** | Failed with Pydantic validation error: `Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'name_or_address': '180ab7180', 'count': 40}, input_type=dict]` for field `address`. |
| **Root Cause** | The Ghidra MCP server schema for `disassemble` expects `address`, while `decompile_function` expects `name_or_address`. |
| **Fix / Remediation** | Patched `mcp._tool_manager.call_tool` in `src/tools/ghidra/mcp_server.py` to pop and normalize `name_or_address` $\to$ `address` for `disassemble` and `read_bytes`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "180ab7180",
    "count": 40
  }
  

Logs:

  Error executing tool disassemble: 1 validation error for disassembleArguments
  address
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'name_or_address': '180ab7180', 'count': 40}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool disassemble
```

## Prevention Invariant
Patched `ghidra/mcp_server.py` normalizes and pops `name_or_address` $\to$ `address`.
