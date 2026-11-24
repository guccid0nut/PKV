---
id: MCP-041
server: ghidra
tool: search_code
category: parameter-mismatch
title: "Ghidra search_code Parameter Schema Validation (pattern vs query)"
status: superseded
superseded_by: "MCP-031"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, search_code, parameter-mismatch]
prevention_rule: "FastMCP hook in `mcp_server.py` normalizes `pattern` $\to$ `query`."
---

# MCP-041: Ghidra search_code Parameter Schema Validation (pattern vs query)

## Summary
Repeat occurrence of [`MCP-031`] on `ghidra` / `search_code` (`parameter-mismatch`). Superseded by `MCP-031`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_code` |
| **Expected** | Search decompiled code for `0x178`. |
| **Actual (Symptom)** | Pydantic schema validation error: `query Field required`. |
| **Root Cause** | `search_code` requires `query` parameter, not `pattern`. |
| **Fix / Remediation** | Use `query: "0x178"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "pattern": "0x178"
  }
  

Logs:

  Error executing tool search_code: 1 validation error for search_codeArguments
  query
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'pattern': '0x178'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_code
```

## Prevention Invariant
FastMCP hook in `mcp_server.py` normalizes `pattern` $\to$ `query`.
