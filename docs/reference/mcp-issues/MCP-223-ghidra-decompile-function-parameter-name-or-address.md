---
id: MCP-223
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "GHIDRA-DECOMPILE-FUNCTION-PARAMETER-NAME-OR-ADDRESS"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Always check `decompile_function.json` schema and pass `name_or_address` when invoking `ghidra / decompile_function`."
---

# MCP-223: GHIDRA-DECOMPILE-FUNCTION-PARAMETER-NAME-OR-ADDRESS

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0x1809c3280` in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned `1 validation error for decompile_functionArguments: name_or_address Field required`. |
| **Root Cause** | The `ghidra / decompile_function` tool schema requires the parameter name `name_or_address`, not `address_or_name`. |
| **Fix / Remediation** | Always check `decompile_function.json` schema and pass `name_or_address` when invoking `ghidra / decompile_function`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"address_or_name": "1809c3280", "binary_name": "/k2_x64.dll"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Always check `decompile_function.json` schema and pass `name_or_address` when invoking `ghidra / decompile_function`.
