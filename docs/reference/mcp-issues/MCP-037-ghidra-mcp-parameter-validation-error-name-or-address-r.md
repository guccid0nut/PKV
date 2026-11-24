---
id: MCP-037
server: ghidra
tool: list_xrefs
category: parameter-mismatch
title: "Ghidra MCP Parameter Validation Error: `name_or_address` Required in `list_xrefs`"
status: superseded
superseded_by: "MCP-022"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, list_xrefs, parameter-mismatch]
prevention_rule: "Always pass `name_or_address` rather than `address` to `ghidra / list_xrefs`."
---

# MCP-037: Ghidra MCP Parameter Validation Error: `name_or_address` Required in `list_xrefs`

## Summary
Repeat occurrence of [`MCP-022`] on `ghidra` / `list_xrefs` (`parameter-mismatch`). Superseded by `MCP-022`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | - |
| **Actual (Symptom)** | - |
| **Root Cause** | The `list_xrefs` tool in `ghidra` MCP server expects the parameter key `name_or_address` rather than `address`. |
| **Fix / Remediation** | Re-invoked `list_xrefs` with parameter `name_or_address: "180f42238"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "address": "180f42238"
  }
  

Logs:

  Error executing tool list_xrefs: 1 validation error for list_xrefsArguments
  name_or_address
    Field required [type=missing, input_value={'address': '180f42238', 'binary_name': 'k2_x64.dll'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
Always pass `name_or_address` rather than `address` to `ghidra / list_xrefs`.
