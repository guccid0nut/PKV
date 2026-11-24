---
id: MCP-164
server: ghidra
tool: search_strings
category: parameter-mismatch
title: "(formerly MCP-101): GHIDRA-SEARCH-STRINGS-QUERY-PARAM-REQUIRED"
status: superseded
superseded_by: "MCP-125"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [ghidra, search_strings, parameter-mismatch]
prevention_rule: "Always pass `query` rather than `string` when calling `ghidra / search_strings`."
---

# MCP-164: (formerly MCP-101): GHIDRA-SEARCH-STRINGS-QUERY-PARAM-REQUIRED

## Summary
Repeat occurrence of [`MCP-125`] on `ghidra` / `search_strings` (`parameter-mismatch`). Superseded by `MCP-125`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_strings` |
| **Expected** | Searches for string "willowmaker" in `k2_x64.dll`. |
| **Actual (Symptom)** | Threw validation error: `query: Field required [type=missing]`. |
| **Root Cause** | `ghidra / search_strings` schema requires parameter named `query`, not `string`. |
| **Fix / Remediation** | Always pass `query` rather than `string` when calling `ghidra / search_strings`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "k2_x64.dll", "string": "willowmaker"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_strings
```

## Prevention Invariant
Always pass `query` rather than `string` when calling `ghidra / search_strings`.
