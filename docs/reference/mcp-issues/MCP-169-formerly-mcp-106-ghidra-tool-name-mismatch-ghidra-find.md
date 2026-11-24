---
id: MCP-169
server: ghidra
tool: ghidra_find_symbol
category: runtime-error
title: "(formerly MCP-106): GHIDRA-TOOL-NAME-MISMATCH-GHIDRA-FIND-SYMBOL"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [ghidra, ghidra_find_symbol, runtime-error]
prevention_rule: "Verify registered Ghidra MCP tool names (`search_symbols_by_name`, `decompile_function`, `list_xrefs`, `search_strings`)."
---

# MCP-169: (formerly MCP-106): GHIDRA-TOOL-NAME-MISMATCH-GHIDRA-FIND-SYMBOL

## Summary
Tracked MCP issue on `ghidra` / `ghidra_find_symbol` (category: `runtime-error`, status: `resolved`, occurrences: 1). Finds matching symbols in Ghidra database. -> Returned error `Unknown tool: ghidra_find_symbol`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `ghidra_find_symbol` |
| **Expected** | Finds matching symbols in Ghidra database. |
| **Actual (Symptom)** | Returned error `Unknown tool: ghidra_find_symbol`. |
| **Root Cause** | Invoked non-existent tool `ghidra_find_symbol` instead of the actual registered tool `search_symbols_by_name`. |
| **Fix / Remediation** | Verify registered Ghidra MCP tool names (`search_symbols_by_name`, `decompile_function`, `list_xrefs`, `search_strings`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "k2_x64.dll", "query": "RegisterArchives"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool ghidra_find_symbol
```

## Prevention Invariant
Verify registered Ghidra MCP tool names (`search_symbols_by_name`, `decompile_function`, `list_xrefs`, `search_strings`).
