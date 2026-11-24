---
id: MCP-030
server: ghidra
tool: search_symbols_by_name
category: parameter-mismatch
title: "Ghidra MCP Parameter Validation Error: `query` Field Required in `search_symbols_by_name`"
status: superseded
superseded_by: "MCP-017"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, search_symbols_by_name, parameter-mismatch]
prevention_rule: "`FastMCP.call_tool` hook normalizes `name_pattern` $\to$ `query`."
---

# MCP-030: Ghidra MCP Parameter Validation Error: `query` Field Required in `search_symbols_by_name`

## Summary
Repeat occurrence of [`MCP-017`] on `ghidra` / `search_symbols_by_name` (`parameter-mismatch`). Superseded by `MCP-017`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_symbols_by_name` |
| **Expected** | Search for symbols matching Connect pattern. |
| **Actual (Symptom)** | Returned error: `1 validation error for search_symbols_by_nameArguments: query: Field required`. |
| **Root Cause** | `search_symbols_by_name` expects `query` (a regex string), not `name_pattern`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook transparently maps `name_pattern`, `symbol_name`, `pattern`, `search`, and `name` to `query` before schema validation executes. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_pattern": "*Connect*"
  }
  

Logs:

  Error executing tool search_symbols_by_name: 1 validation error for search_symbols_by_nameArguments
  query
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'name_pattern': '*Connect*'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_symbols_by_name
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `name_pattern` $\to$ `query`.
