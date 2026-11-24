---
id: MCP-075
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "`ghidra` decompile_function Timeout on `0x180ab7180`"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Disassemble call sites and inspect parameter registers instead of full decompilation."
---

# MCP-075: `ghidra` decompile_function Timeout on `0x180ab7180`

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Pseudo-C decompilation within the 60s timeout window. |
| **Actual (Symptom)** | Timed out with `Exception while decompiling 180ab7180: process: timeout`. |
| **Root Cause** | `0x180ab7180` is a massive network packet crypt/filter routine with thousands of basic blocks and control-flow branches, exceeding Ghidra's 60s decompiler timeout. |
| **Fix / Remediation** | Disassemble specific call sites and inspect parameter registers instead of full decompilation. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "0x180ab7180"
  }
  

Logs:

  {
    "name": "FUN_180ab7180-180ab7180",
    "code": "Exception while decompiling 180ab7180: process: timeout\n"
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Disassemble call sites and inspect parameter registers instead of full decompilation.
