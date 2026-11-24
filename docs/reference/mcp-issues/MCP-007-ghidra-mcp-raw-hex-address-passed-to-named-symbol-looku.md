---
id: MCP-007
server: ghidra
tool: find_symbol
category: decompiler-timeout
title: "Ghidra MCP Raw Hex Address Passed to Named Symbol Lookup ('180f739f0' / '0x180f739f0')"
status: superseded
superseded_by: "MCP-080"
supersedes: []
occurrences: 1
date: 2026-09-24
last_seen: 2026-09-24
tags: [ghidra, find_symbol, decompiler-timeout]
prevention_rule: "`mcp_server.py` resilient `find_symbol` auto-resolves hex addresses via AddressFactory directly to symbol table."
---

# MCP-007: Ghidra MCP Raw Hex Address Passed to Named Symbol Lookup ('180f739f0' / '0x180f739f0')

## Summary
Repeat occurrence of [`MCP-080`] on `ghidra` / `find_symbol` (`decompiler-timeout`). Superseded by `MCP-080`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `find_symbol` |
| **Expected** | Locating xrefs and symbols at hex address offsets `0x180f739f0` and `0x180f73a14`. |
| **Actual (Symptom)** | Repeated failure with errors: |
| **Root Cause** | `find_symbol` queries Ghidra's symbol table for an exact string label (e.g., `g_pHost`, `svr_name`, `Ordinal_1234`). When passed a raw hexadecimal address string without a named label attached, Ghidra searches for a symbol whose literal name is `"180f739f0"` or `"0x180f739f0"`, which does not exist. - Furthermore, `list_xrefs` fails when passed an... |
| **Fix / Remediation** | Enforced in Ghidra MCP operating rules: pass hex addresses to `disassemble` / `decompile_function`, never `find_symbol`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "symbol_name": "180f739f0"
  },
  {
    "symbol_name": "180f73a14"
  },
  {
    "symbol_name": "0x180f739f0"
  }
  

Logs:

  [23:23:13] [ASPIRE HOST] [ERROR] 2026-09-24 18:23:12,949 [ERROR] [pyghidra_mcp.tools] Error in find_symbol: Symbol '180f739f0' not found.
  [23:23:13] [ASPIRE HOST] [ERROR] 2026-09-24 18:23:12,949 [ERROR] [pyghidra_mcp.tools] Error in list_xrefs: Symbol '180f739f0' not found.
  [23:23:57] [ASPIRE HOST] [ERROR] 2026-09-24 18:23:57,007 [ERROR] [pyghidra_mcp.tools] Error in find_symbol: Symbol '180f73a14' not found.
  [23:23:57] [ASPIRE HOST] [ERROR] 2026-09-24 18:23:57,008 [ERROR] [pyghidra_mcp.tools] Error in list_xrefs: Symbol '180f73a14' not found.
  [23:30:35] [ASPIRE HOST] [ERROR] 2026-09-24 18:30:34,458 [ERROR] [pyghidra_mcp.tools] Error in find_symbol: Symbol '180f739f0' not found.
  [23:30:35] [ASPIRE HOST] [ERROR] 2026-09-24 18:30:34,458 [ERROR] [pyghidra_mcp.tools] Error in list_xrefs: Symbol '180f739f0' not found.
  [23:30:37] [ASPIRE HOST] [ERROR] 2026-09-24 18:30:36,537 [ERROR] [pyghidra_mcp.tools] Error in find_symbol: Symbol '0x180f739f0' not found.
  [23:30:37] [ASPIRE HOST] [ERROR] 2026-09-24 18:30:36,537 [ERROR] [pyghidra_mcp.tools] Error in list_xrefs: Symbol '0x180f739f0' not found.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool find_symbol
```

## Prevention Invariant
`mcp_server.py` resilient `find_symbol` auto-resolves hex addresses via AddressFactory directly to symbol table.
