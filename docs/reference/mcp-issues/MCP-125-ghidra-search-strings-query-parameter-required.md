---
id: MCP-125
server: ghidra
tool: search_strings
category: parameter-mismatch
title: "GHIDRA-SEARCH-STRINGS-QUERY-PARAMETER-REQUIRED"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-160", "MCP-164"]
occurrences: 3
date: 2026-10-11
last_seen: 2026-10-11
tags: [ghidra, search_strings, parameter-mismatch]
prevention_rule: "Always provide `query` parameter to `search_strings`."
---

# MCP-125: GHIDRA-SEARCH-STRINGS-QUERY-PARAMETER-REQUIRED

## Summary
Tracked MCP issue on `ghidra` / `search_strings` (category: `parameter-mismatch`, status: `resolved`, occurrences: 3). Search for strings containing "sleep". -> Validation error: `query` Field required.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_strings` |
| **Expected** | Search for strings containing "sleep". |
| **Actual (Symptom)** | Validation error: `query` Field required. |
| **Root Cause** | `search_strings` schema expects `query`, not `filter`. |
| **Fix / Remediation** | Always provide `query` parameter to `search_strings`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "k2_x64.dll", "filter": "sleep"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_strings
```

## Prevention Invariant
Always provide `query` parameter to `search_strings`.
