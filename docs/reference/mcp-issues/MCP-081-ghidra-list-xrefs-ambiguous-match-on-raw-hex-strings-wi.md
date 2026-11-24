---
id: MCP-081
server: ghidra
tool: list_xrefs
category: transport-eof
title: "`ghidra` list_xrefs Ambiguous Match on Raw Hex Strings Without '0x' Prefix"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, list_xrefs, transport-eof]
prevention_rule: "Inspect running processes directly via PowerShell cmdlets (`Get-Process`, `Get-CimInstance`) and use Nexus IPC / engine console commands directly if MCP daemon stdin/stdout stream is terminated."
---

# MCP-081: `ghidra` list_xrefs Ambiguous Match on Raw Hex Strings Without '0x' Prefix

## Summary
Tracked MCP issue on `ghidra` / `list_xrefs` (category: `transport-eof`, status: `resolved`, occurrences: 1). Retrieve cross-references for virtual address `0x1808e3740`. -> Returned `Ambiguous match for '1808e3740'. Did you mean one of these: CHostServer::StartGame @ 1808e3740, Ordinal_3103 @ 1808e3740, ?StartGame@CHostServer@@QEAAXIV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z @ 1808e3740`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_xrefs` |
| **Expected** | Retrieve cross-references for virtual address `0x1808e3740`. |
| **Actual (Symptom)** | Returned `Ambiguous match for '1808e3740'. Did you mean one of these: CHostServer::StartGame @ 1808e3740, Ordinal_3103 @ 1808e3740, ?StartGame@CHostServer@@QEAAXIV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z @ 1808e3740`. |
| **Root Cause** | The parameter `1808e3740` was passed without a `0x` prefix. The fast disambiguation logic checks for `startswith("0x")` or integer parsing before grouping identical addresses, falling through to string pattern matching which caught all three alias symbols. |
| **Fix / Remediation** | Canonicalize address queries with leading `0x` before calling Ghidra MCP tools. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "1808e3740"
  }
  

Logs:

  Error in list_xrefs: Ambiguous match for '1808e3740'. Did you mean one of these: CHostServer::StartGame @ 1808e3740, Ordinal_3103 @ 1808e3740
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_xrefs
```

## Prevention Invariant
Inspect running processes directly via PowerShell cmdlets (`Get-Process`, `Get-CimInstance`) and use Nexus IPC / engine console commands directly if MCP daemon stdin/stdout stream is terminated.
