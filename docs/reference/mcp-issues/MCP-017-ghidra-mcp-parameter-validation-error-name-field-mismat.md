---
id: MCP-017
server: ghidra
tool: search_symbols_by_name
category: parameter-mismatch
title: "Ghidra MCP Parameter Validation Error: 'name' Field Mismatch in `search_symbols_by_name`"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-030", "MCP-123"]
occurrences: 3
date: 2026-09-24
last_seen: 2026-10-11
tags: [ghidra, search_symbols_by_name, parameter-mismatch]
prevention_rule: "`FastMCP.call_tool` hook normalizes `name` / `name_pattern` $\to$ `query`."
---

# MCP-017: Ghidra MCP Parameter Validation Error: 'name' Field Mismatch in `search_symbols_by_name`

## Summary
Tracked MCP issue on `ghidra` / `search_symbols_by_name` (category: `parameter-mismatch`, status: `patched`, occurrences: 3). Search for symbols matching "ReadClientPackets". -> Threw schema validation error: `1 validation error for search_symbols_by_nameArguments: query: Field required`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_symbols_by_name` |
| **Expected** | Search for symbols matching "ReadClientPackets". |
| **Actual (Symptom)** | Threw schema validation error: `1 validation error for search_symbols_by_nameArguments: query: Field required`. |
| **Root Cause** | The Ghidra MCP server tool schema defines `query` as the search string parameter for `search_symbols_by_name`, not `name`. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook maps `name`, `symbol_name`, `pattern`, `search`, and `name_pattern` to `query` prior to schema validation. - Verified by `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name": "ReadClientPackets"
  }
  

Logs:

  Error executing tool search_symbols_by_name: 1 validation error for search_symbols_by_nameArguments
  query
    Field required [type=missing, input_value={'binary_name': 'k2_x64.dll', 'name': 'ReadClientPackets'}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_symbols_by_name
```

## Prevention Invariant
`FastMCP.call_tool` hook normalizes `name` / `name_pattern` $\to$ `query`.
