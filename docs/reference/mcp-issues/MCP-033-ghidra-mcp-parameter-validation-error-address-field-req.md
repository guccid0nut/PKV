---
id: MCP-033
server: ghidra
tool: disassemble
category: decompiler-timeout
title: "Ghidra MCP Parameter Validation Error: `address` Field Required in `disassemble`"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-055"]
occurrences: 3
date: 2026-09-25
last_seen: 2026-11-06
tags: [ghidra, disassemble, decompiler-timeout]
prevention_rule: "`FastMCP.call_tool` hook normalizes `name_or_address` $\to$ `address` and `limit` $\to$ `count`."
---

# MCP-033: Ghidra MCP Parameter Validation Error: `address` Field Required in `disassemble`

## Summary
Tracked MCP issue on `ghidra` / `disassemble` (category: `decompiler-timeout`, status: `investigating`, occurrences: 3). Disassemble instructions at `0x180ab7180`. -> Returned error: `1 validation error for disassembleArguments: address: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `disassemble` |
| **Expected** | Disassemble instructions at `0x180ab7180`. |
| **Actual (Symptom)** | Returned error: `1 validation error for disassembleArguments: address: Field required`. |
| **Root Cause** | Unlike `decompile_function` and `list_xrefs` which require `name_or_address`, `disassemble` expects `address`, and uses `count` instead of `limit`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `name_or_address` $\to$ `address` and `limit` $\to$ `count` for `disassemble` calls before schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "180ab7180",
    "limit": 40
  }
  

Logs:

  Error executing tool disassemble: 1 validation error for disassembleArguments
  address
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'limit': 40, 'name_or_address': '180ab7180'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool disassemble
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `name_or_address` $\to$ `address` and `limit` $\to$ `count`.
