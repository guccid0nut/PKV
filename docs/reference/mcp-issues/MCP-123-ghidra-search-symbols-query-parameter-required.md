---
id: MCP-123
server: ghidra
tool: search_symbols_by_name
category: parameter-mismatch
title: "GHIDRA-SEARCH-SYMBOLS-QUERY-PARAMETER-REQUIRED"
status: superseded
superseded_by: "MCP-017"
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [ghidra, search_symbols_by_name, parameter-mismatch]
prevention_rule: "Pass `query` parameter to `search_symbols_by_name`."
---

# MCP-123: GHIDRA-SEARCH-SYMBOLS-QUERY-PARAMETER-REQUIRED

## Summary
Repeat occurrence of [`MCP-017`] on `ghidra` / `search_symbols_by_name` (`parameter-mismatch`). Superseded by `MCP-017`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_symbols_by_name` |
| **Expected** | Search symbols by name using `name_or_address`. |
| **Actual (Symptom)** | Validation error: `query` Field required. |
| **Root Cause** | `search_symbols_by_name` expects `query`, not `name_or_address`. |
| **Fix / Remediation** | Pass `query` parameter to `search_symbols_by_name`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "k2_x64.dll", "name_or_address": "CHost::Execute"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_symbols_by_name
```

## Prevention Invariant
Pass `query` parameter to `search_symbols_by_name`.
