---
id: MCP-070
server: ghidra
tool: search_strings
category: binary-decode
title: "`ghidra` search_strings Validation Error: Missing Required Argument `binary_name`"
status: superseded
superseded_by: "MCP-021"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, search_strings, binary-decode]
prevention_rule: "`mcp_server.py` auto-defaults `binary_name` to `'k2_x64.dll'` for all tools requiring a binary if omitted."
---

# MCP-070: `ghidra` search_strings Validation Error: Missing Required Argument `binary_name`

## Summary
Repeat occurrence of [`MCP-021`] on `ghidra` / `search_strings` (`binary-decode`). Superseded by `MCP-021`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_strings` |
| **Expected** | Ghidra searches indexed strings for "timed out during state" across default binary or project. |
| **Actual (Symptom)** | Failed with Pydantic validation error: `1 validation error for search_stringsArguments: binary_name: Field required`. |
| **Root Cause** | `search_strings` in `src/tools/ghidra/mcp_server.py` requires an explicit `binary_name` parameter (e.g. `k2_x64.dll` or `game_x64.dll`). |
| **Fix / Remediation** | Provide `binary_name: "k2_x64.dll"` in all future `search_strings` invocations. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "query": "timed out during state"
  }
  

Logs:

  Error executing tool search_strings: 1 validation error for search_stringsArguments
  binary_name
    Field required [type=missing, input_value={'query': 'timed out during state'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_strings
```

## Prevention Invariant
`mcp_server.py` auto-defaults `binary_name` to `'k2_x64.dll'` for all tools requiring a binary if omitted.
