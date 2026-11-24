---
id: MCP-064
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra decompile_function Timeout on FUN_180ab7180 in k2_x64.dll"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "FastMCP hook aliases `timeout` $\to$ `timeout_sec` (default 60s); disassemble caller basic blocks for monolithic crypto."
---

# MCP-064: Ghidra decompile_function Timeout on FUN_180ab7180 in k2_x64.dll

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `180ab7180` in `k2_x64.dll`. |
| **Actual (Symptom)** | Timed out after 60 seconds with `Exception while decompiling 180ab7180: process: timeout`. |
| **Root Cause** | `FUN_180ab7180` is a monolithic network / crypto / packet security validation routine with extensive control-flow complexity, causing Ghidra's decompiler AST simplification passes to exceed the default execution budget. In addition, callers often passed `timeout` instead of `timeout_sec`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: Intercepts `decompile_function` in FastMCP and tool manager, normalizing parameter alias `timeout` $\to$ `timeout_sec` and extending default timeout to 60s. |

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
FastMCP hook aliases `timeout` $\to$ `timeout_sec` (default 60s); disassemble caller basic blocks for monolithic crypto.
