---
id: MCP-038
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Decompilation Timeout on FUN_180ab7180"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Disassemble caller blocks via `disassemble` for monolithic crypto routines."
---

# MCP-038: Ghidra MCP Decompilation Timeout on FUN_180ab7180

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Return decompiled C pseudo-code for `0x180ab7180` in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned timeout: `Exception while decompiling 180ab7180: process: timeout`. |
| **Root Cause** | `FUN_180ab7180` is a massive unrolled cryptographic hashing function. Even with extended timeout, decompilation of the AST exceeds limits. |
| **Fix / Remediation** | Pivoted to inspecting caller logic in `FUN_18095dd30` and disassembling relevant control flow blocks. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "0x180ab7180"
  }
  

Logs:

  Exception while decompiling 180ab7180: process: timeout
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Disassemble caller blocks via `disassemble` for monolithic crypto routines.
