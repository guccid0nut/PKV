---
id: MCP-027
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Decompiler Process Timeout on Heavy Cryptographic Function (`180ab7180`)"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Default 60s timeout in `mcp_server.py`; inspect assembly via `disassemble` for heavy crypt routines."
---

# MCP-027: Ghidra MCP Decompiler Process Timeout on Heavy Cryptographic Function (`180ab7180`)

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Return decompiled C pseudo-code for function `180ab7180` in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned timeout message: `Exception while decompiling 180ab7180: process: timeout`. |
| **Root Cause** | `FUN_180ab7180` is a massive cryptographic / watermark hashing algorithm with deeply nested control flow and unrolled loops, exceeding the default Ghidra decompiler timeout (30 seconds). |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: Increased default decompile timeout in `FastMCP.call_tool` hook to 60 seconds. Caller xref tracing and assembly disassembly also documented. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "180ab7180"
  }
  

Logs:

  Exception while decompiling 180ab7180: process: timeout
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Default 60s timeout in `mcp_server.py`; inspect assembly via `disassemble` for heavy crypt routines.
