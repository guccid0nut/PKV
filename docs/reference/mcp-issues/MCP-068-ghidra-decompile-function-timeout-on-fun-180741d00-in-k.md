---
id: MCP-068
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "`ghidra` decompile_function Timeout on FUN_180741d00 in k2_x64.dll"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Slicing and basic-block disassembly for monolithic network connection routines."
---

# MCP-068: `ghidra` decompile_function Timeout on FUN_180741d00 in k2_x64.dll

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile `FUN_180741d00` (monolithic client connection logic in `k2_x64.dll`) within 60s. |
| **Actual (Symptom)** | Timed out after 60s with `Exception while decompiling 180741d00: process: timeout`. |
| **Root Cause** | `FUN_180741d00` is a massive monolithic networking routine spanning thousands of basic blocks in `k2_x64.dll` (~19.8 MB binary), causing the headless Ghidra decompiler subprocess to exceed its 60-second limit. |
| **Fix / Remediation** | Pivoted to Capstone disassembly around cross-reference offset `0x180742ce1` where the connection packet is formed. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "0x180741d00"
  }
  

Logs:

  Exception while decompiling 180741d00: process: timeout
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Slicing and basic-block disassembly for monolithic network connection routines.
