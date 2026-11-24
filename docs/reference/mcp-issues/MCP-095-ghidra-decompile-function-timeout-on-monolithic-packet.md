---
id: MCP-095
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "`ghidra` / `decompile_function` Timeout on Monolithic Packet Crypt/Unpack Routine `0x180ab7180`"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Inspect smaller caller and callee routines or disassemble targeted entry blocks with `disassemble` / `read_bytes` instead of full function decompilation for monolithic crypto routines."
---

# MCP-095: `ghidra` / `decompile_function` Timeout on Monolithic Packet Crypt/Unpack Routine `0x180ab7180`

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function `0x180ab7180` within timeout. |
| **Actual (Symptom)** | Failed with `Exception while decompiling 180ab7180: process: timeout` after 60 seconds. |
| **Root Cause** | `FUN_180ab7180` is a massive monolithic crypto/compression and packet framing routine in `k2_x64.dll`. Ghidra's decompiler AST construction for this function exceeds the 60s subprocess timeout. |
| **Fix / Remediation** | Avoid monolithic decompile on `0x180ab7180`; analyze packet flow through higher-level socket caller functions. |

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
Inspect smaller caller and callee routines or disassemble targeted entry blocks with `disassemble` / `read_bytes` instead of full function decompilation for monolithic crypto routines.
