---
id: MCP-160
server: ghidra
tool: search_strings
category: parameter-mismatch
title: "(formerly MCP-097): GHIDRA-SEARCH-STRINGS-QUERY-REQUIRED"
status: superseded
superseded_by: "MCP-125"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [ghidra, search_strings, parameter-mismatch]
prevention_rule: "Always use `query` for string patterns when calling `ghidra / search_strings`."
---

# MCP-160: (formerly MCP-097): GHIDRA-SEARCH-STRINGS-QUERY-REQUIRED

## Summary
Repeat occurrence of [`MCP-125`] on `ghidra` / `search_strings` (`parameter-mismatch`). Superseded by `MCP-125`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_strings` |
| **Expected** | Searches strings matching pattern in binary. |
| **Actual (Symptom)** | Threw validation error: `query: Field required [type=missing]`. |
| **Root Cause** | `search_strings` requires parameter `query` rather than `filter`. |
| **Fix / Remediation** | Always use `query` for string patterns when calling `ghidra / search_strings`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "cgame_x64.dll", "filter": "Old:"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_strings
```

## Prevention Invariant
Always use `query` for string patterns when calling `ghidra / search_strings`.
