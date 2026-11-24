---
id: MCP-218
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "GHIDRA-DECOMPILE-FUNCTION-TIMEOUT-ON-MASSIVE-CVAR-REGISTRATION-INIT"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Never call `decompile_function` on global CRT static initializer routines (`0x1807ba6a0`); use `disassemble` with `count: 25` at the specific `lea rdx, ['cvar_name']` cross-reference address instead."
---

# MCP-218: GHIDRA-DECOMPILE-FUNCTION-TIMEOUT-ON-MASSIVE-CVAR-REGISTRATION-INIT

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile `FUN_1807ba6a0` (`_dynamic_initializer_for__php_printDebugInfo__` / global CVar static initializer block in `k2_x64.dll`). |
| **Actual (Symptom)** | Returned `Exception while decompiling 1807ba6a0: process: timeout` after 60 seconds. |
| **Root Cause** | `0x1807ba6a0` is a massive MSVC compiler-generated CRT static initializer function that constructs hundreds of global `CCvar` and `CConsoleElement` objects sequentially, exceeding the 60-second Ghidra decompiler AST simplification timeout. |
| **Fix / Remediation** | Never call `decompile_function` on global CRT static initializer routines (`0x1807ba6a0`); use `disassemble` with `count: 25` at the specific `lea rdx, ["cvar_name"]` cross-reference address instead. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "/k2_x64.dll-64104e", "name_or_address": "0x1807ba6a0"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Never call `decompile_function` on global CRT static initializer routines (`0x1807ba6a0`); use `disassemble` with `count: 25` at the specific `lea rdx, ['cvar_name']` cross-reference address instead.
