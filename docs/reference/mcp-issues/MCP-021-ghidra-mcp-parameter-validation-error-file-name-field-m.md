---
id: MCP-021
server: ghidra
tool: search_strings
category: binary-decode
title: "Ghidra MCP Parameter Validation Error: 'file_name' Field Mismatch in `search_strings`"
status: investigating
superseded_by: ""
supersedes: ["MCP-070"]
occurrences: 2
date: 2026-09-24
last_seen: 2026-09-25
tags: [ghidra, search_strings, binary-decode]
prevention_rule: "`FastMCP.call_tool` hook normalizes `file_name` $\to$ `binary_name`."
---

# MCP-021: Ghidra MCP Parameter Validation Error: 'file_name' Field Mismatch in `search_strings`

## Summary
Tracked MCP issue on `ghidra` / `search_strings` (category: `binary-decode`, status: `patched`, occurrences: 2). Search for string "ServerReset" in `k2_x64.dll`. -> Threw schema validation error: `1 validation error for search_stringsArguments: binary_name: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_strings` |
| **Expected** | Search for string "ServerReset" in `k2_x64.dll`. |
| **Actual (Symptom)** | Threw schema validation error: `1 validation error for search_stringsArguments: binary_name: Field required`. |
| **Root Cause** | The Ghidra MCP server tool schema defines `binary_name` as the target binary parameter for `search_strings`, not `file_name`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `file_name`, `program`, `binary` to `binary_name` prior to schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "file_name": "k2_x64.dll",
    "query": "ServerReset"
  }
  

Logs:

  Error executing tool search_strings: 1 validation error for search_stringsArguments
  binary_name
    Field required [type=missing, input_value={'file_name': 'k2_x64.dll', 'query': 'ServerReset'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_strings
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `file_name` $\to$ `binary_name`.
