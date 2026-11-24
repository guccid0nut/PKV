---
id: MCP-219
server: ghidra
tool: list_xrefs
category: binary-decode
title: "GHIDRA-LIST-XREFS-STALE-BINARY-HASH-SUFFIX"
status: superseded
superseded_by: "MCP-011"
supersedes: []
occurrences: 1
date: 2026-10-15
last_seen: 2026-10-15
tags: [ghidra, list_xrefs, binary-decode]
prevention_rule: "Always pass `binary_name: '/k2_x64.dll-64104e'` (or verify via `list_project_binaries`) when calling `ghidra` MCP tools against `k2_x64.dll`."
---

# MCP-219: GHIDRA-LIST-XREFS-STALE-BINARY-HASH-SUFFIX

## Summary
Repeat occurrence of [`MCP-011`] on `ghidra` / `list_xrefs` (`binary-decode`). Superseded by `MCP-011`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | List cross-references to `php_printDebugInfo` value byte `0x1812bcdb9` in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned `Error executing tool list_xrefs: Binary k2_x64.dll-38f05204bd4d9fb5 not found`. |
| **Root Cause** | `ProjectKongor.rep` indexes `k2_x64.dll` under the program name `"/k2_x64.dll-64104e"`, not the legacy hash suffix `"k2_x64.dll-38f05204bd4d9fb5"`. |
| **Fix / Remediation** | Always pass `binary_name: "/k2_x64.dll-64104e"` (or verify via `list_project_binaries`) when calling `ghidra` MCP tools against `k2_x64.dll`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"address": "0x1812bcdb9", "binary_name": "k2_x64.dll-38f05204bd4d9fb5"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
Always pass `binary_name: '/k2_x64.dll-64104e'` (or verify via `list_project_binaries`) when calling `ghidra` MCP tools against `k2_x64.dll`.
