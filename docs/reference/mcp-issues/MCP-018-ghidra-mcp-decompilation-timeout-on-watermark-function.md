---
id: MCP-018
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Decompilation Timeout on Watermark Function (0x180ab7180)"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Default 60s timeout in `mcp_server.py`; inspect assembly via `disassemble` for monolithic crypto."
---

# MCP-018: Ghidra MCP Decompilation Timeout on Watermark Function (0x180ab7180)

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile complex native function `FUN_180ab7180`. |
| **Actual (Symptom)** | Failed with `Exception while decompiling 180ab7180: process: timeout`. |
| **Root Cause** | `FUN_180ab7180` is an extraordinarily large, unrolled cryptographic/hashing watermark validation routine in `k2_x64.dll` exceeding the default Ghidra decompiler 30-second execution window. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook extends default timeout to 60s for `decompile_function` calls. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

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
Default 60s timeout in `mcp_server.py`; inspect assembly via `disassemble` for monolithic crypto.
