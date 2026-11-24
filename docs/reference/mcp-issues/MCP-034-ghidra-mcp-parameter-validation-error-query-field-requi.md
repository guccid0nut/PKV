---
id: MCP-034
server: ghidra
tool: search_symbols_by_name
category: binary-decode
title: "Ghidra MCP Parameter Validation Error: `query` Field Required in `search_symbols_by_name`"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-140"]
occurrences: 3
date: 2026-09-25
last_seen: 2026-11-04
tags: [ghidra, search_symbols_by_name, binary-decode]
prevention_rule: "Pass `query` rather than `name` to `search_symbols_by_name`."
---

# MCP-034: Ghidra MCP Parameter Validation Error: `query` Field Required in `search_symbols_by_name`

## Summary
Tracked MCP issue on `ghidra` / `search_symbols_by_name` (category: `binary-decode`, status: `investigating`, occurrences: 3). Search symbols matching "CreateGame" in `k2_x64.dll`. -> Returned error: `1 validation error for search_symbols_by_nameArguments: query: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_symbols_by_name` |
| **Expected** | Search symbols matching "CreateGame" in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned error: `1 validation error for search_symbols_by_nameArguments: query: Field required`. |
| **Root Cause** | The schema for `search_symbols_by_name` takes `query` and `binary_name`, not `name`. |
| **Fix / Remediation** | Updated tool parameter usage to `query`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name": "CreateGame"
  }
  

Logs:

  Error executing tool search_symbols_by_name: 1 validation error for search_symbols_by_nameArguments
  query
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'name': 'CreateGame'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_symbols_by_name
```

## Prevention Invariant
Pass `query` rather than `name` to `search_symbols_by_name`.
