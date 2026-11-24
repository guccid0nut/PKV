---
id: MCP-139
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "GHIDRA-DECOMPILE-FUNCTION-SYMBOL-NAME-REQUIRES-HEX-ADDRESS"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Always call `search_symbols_by_name` first to resolve the exact hex address (`0x180...`) and pass that hex address as `name_or_address` to `decompile_function`."
---

# MCP-139: GHIDRA-DECOMPILE-FUNCTION-SYMBOL-NAME-REQUIRES-HEX-ADDRESS

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile `CLuaScriptManager::CountMemoryUsed`. |
| **Actual (Symptom)** | Returned `"Function or symbol 'CountMemoryUsed' not found."` because Ghidra stores class methods under `<ClassName>::<MethodName>-<HexAddr>` and labels at the address. |
| **Root Cause** | `decompile_function` expects either the exact hex address (`0x180...`) or the full internal Ghidra function name (`ICvar::GetInteger-18085ece0`). |
| **Fix / Remediation** | Always call `search_symbols_by_name` first to resolve the exact hex address (`0x180...`) and pass that hex address as `name_or_address` to `decompile_function`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "/k2_x64.dll-64104e", "name_or_address": "CountMemoryUsed"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Always call `search_symbols_by_name` first to resolve the exact hex address (`0x180...`) and pass that hex address as `name_or_address` to `decompile_function`.
