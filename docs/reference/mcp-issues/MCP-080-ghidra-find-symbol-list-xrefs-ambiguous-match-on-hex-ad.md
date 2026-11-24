---
id: MCP-080
server: ghidra
tool: find_symbol
category: decompiler-timeout
title: "`ghidra` find_symbol & list_xrefs Ambiguous Match on Hex Addresses & Overloaded Functions"
status: codified
codified_in: "src/tools/ghidra/mcp_server.py"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-007"]
occurrences: 6
date: 2026-09-25
last_seen: 2026-11-17
tags: [ghidra, find_symbol, decompiler-timeout]
prevention_rule: "`mcp_server.py` hooks `GhidraTools` to disambiguate multiple symbols sharing virtual address and resolve overloaded functions."
---

# MCP-080: `ghidra` find_symbol & list_xrefs Ambiguous Match on Hex Addresses & Overloaded Functions

## Summary
Tracked MCP issue on `ghidra` / `find_symbol` (category: `decompiler-timeout`, status: `codified`, occurrences: 6). Resolve symbol or cross-references for virtual address `0x1808ea4f0` or overloaded function `AtoBFast`. -> Failed with `ValueError: Ambiguous match for '0x1808ea4f0'. Did you mean one of these: CHostServer::GenerateClientID @ 1808ea4f0, Ordinal_994 @ 1808ea4f0, ?GenerateClientID@CHostServer@@QEAAHAEAGH@Z @ 1808ea4f0`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `find_symbol` |
| **Expected** | Resolve symbol or cross-references for virtual address `0x1808ea4f0` or overloaded function `AtoBFast`. |
| **Actual (Symptom)** | Failed with `ValueError: Ambiguous match for '0x1808ea4f0'. Did you mean one of these: CHostServer::GenerateClientID @ 1808ea4f0, Ordinal_994 @ 1808ea4f0, ?GenerateClientID@CHostServer@@QEAAHAEAGH@Z @ 1808ea4f0`. |
| **Root Cause** | In Ghidra, querying by address returns all symbols assigned to that address (demangled C++ symbol, ordinal export, and mangled symbol). Because `len(matches) > 1`, `pyghidra_mcp` raised an `Ambiguous match` ValueError despite all symbols pointing to the exact same virtual address. Similarly, querying overloaded C++ functions (`AtoBFast`, `XtoW`) th... |
| **Fix / Remediation** | Hooked `pyghidra_mcp.tools.GhidraTools.find_symbol`, `find_function`, and `list_xrefs` in `src/tools/ghidra/mcp_server.py` to auto-disambiguate multiple symbols sharing the same address and resolve overloaded functions cleanly. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "name_or_address": "0x1808ea4f0"
  }
  

Logs:

  Error in find_symbol: Ambiguous match for '0x1808ea4f0'. Did you mean one of these: CHostServer::GenerateClientID @ 1808ea4f0, Ordinal_994 @ 1808ea4f0, ?GenerateClientID@CHostServer@@QEAAHAEAGH@Z @ 1808ea4f0
  Error in list_xrefs: Ambiguous match for '0x1808ea4f0'. Did you mean one of these: CHostServer::GenerateClientID @ 1808ea4f0, Ordinal_994 @ 1808ea4f0
  Error in find_function: Ambiguous match for 'AtoBFast'. Did you mean one of these: AtoBFast(bool __cdecl AtoBFast(char * param_1)) @ 180c6e920, AtoBFast(bool __cdecl AtoBFast(wchar_t * param_1)) @ 180c722f0
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool find_symbol
```

## Prevention Invariant
`mcp_server.py` hooks `GhidraTools` to disambiguate multiple symbols sharing virtual address and resolve overloaded functions.
