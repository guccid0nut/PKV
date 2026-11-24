---
id: MCP-043
server: ghidra
tool: list_xrefs
category: parameter-mismatch
title: "Ghidra list_xrefs Parameter Schema Validation (address vs name_or_address)"
status: superseded
superseded_by: "MCP-022"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, list_xrefs, parameter-mismatch]
prevention_rule: "FastMCP hook in `mcp_server.py` normalizes `address` $\to$ `name_or_address`."
---

# MCP-043: Ghidra list_xrefs Parameter Schema Validation (address vs name_or_address)

## Summary
Repeat occurrence of [`MCP-022`] on `ghidra` / `list_xrefs` (`parameter-mismatch`). Superseded by `MCP-022`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | Retrieve cross references for address `180f40cb0`. |
| **Actual (Symptom)** | Validation error: `name_or_address Field required`. |
| **Root Cause** | `list_xrefs` parameter is named `name_or_address`. |
| **Fix / Remediation** | Re-invoke using `name_or_address: "180f40cb0"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "address": "180f40cb0"
  }
  

Logs:

  Error executing tool list_xrefs: 1 validation error for list_xrefsArguments
  name_or_address
    Field required [type=missing, input_value={'address': '180f40cb0', 'binary_name': 'k2_x64.dll'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
FastMCP hook in `mcp_server.py` normalizes `address` $\to$ `name_or_address`.
