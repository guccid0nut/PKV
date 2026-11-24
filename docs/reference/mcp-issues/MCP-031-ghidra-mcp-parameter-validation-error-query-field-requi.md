---
id: MCP-031
server: ghidra
tool: search_code
category: parameter-mismatch
title: "Ghidra MCP Parameter Validation Error: `query` Field Required in `search_code`"
status: investigating
superseded_by: ""
supersedes: ["MCP-041"]
occurrences: 2
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, search_code, parameter-mismatch]
prevention_rule: "`FastMCP.call_tool` hook normalizes `pattern` $\to$ `query`."
---

# MCP-031: Ghidra MCP Parameter Validation Error: `query` Field Required in `search_code`

## Summary
Tracked MCP issue on `ghidra` / `search_code` (category: `parameter-mismatch`, status: `patched`, occurrences: 2). Search pseudo-C code for references to 0x2aa. -> Returned error: `1 validation error for search_codeArguments: query: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_code` |
| **Expected** | Search pseudo-C code for references to 0x2aa. |
| **Actual (Symptom)** | Returned error: `1 validation error for search_codeArguments: query: Field required`. |
| **Root Cause** | `search_code` expects `query`, not `pattern`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `pattern` $\to$ `query` before schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "pattern": "2aa"
  }
  

Logs:

  Error executing tool search_code: 1 validation error for search_codeArguments
  query
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'limit': 20, 'pattern': '2aa'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_code
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `pattern` $\to$ `query`.
